# Victor — Architecture

> Keep this file in sync with the codebase. Update it every time a
> component is added, modified, or removed.

## DI Convention

All services use **Firefly.DependencyInjection** attributes (`[RegisterSingleton]`,
`[RegisterScoped]`, `[RegisterTransient]`) instead of manual `IServiceCollection`
extensions.

```csharp
// Register with interface binding
[RegisterScoped(Type = typeof(IMyService))]

// Host registers all assemblies via builder
services.AddFireflyServiceRegistration(b =>
{
    b.UseAssembly(typeof(SomeTypeInAssembly).Assembly);
    // ... one call per project assembly
});
```

Multiple implementations of the same interface resolve to `ICollection<T>` automatically.
Use `b.PickSingleImplementation<TInterface, TConcrete>()` for conditional selection.

## Component Map

```
                         +------------------+
                         |   Victor.Host    |  ASP.NET Core host, DI, config,
                         |   (entry point)  |  health checks, IHostedService
                         +--------+---------+
                                  |
                    +-------------+-------------+
                    |                           |
           +--------v--------+        +---------v--------+
           |  Victor.Slack   |        | Victor.Persona.* |
           |  (comms layer)  |        | (system prompts)  |
           +--------+--------+        +------------------+
                    |
           +--------v--------+
           |   Victor.Core   |  Orchestrator, JobQueue,
           |   (brain)       |  ILLMProvider, ITool
           +---+------+------+
               |      |
       +-------+      +--------+
       |                       |
+------v--------+    +---------v---------+
| Victor.       |    | Victor.Tools.*    |
| Providers.*   |    | Shell, Memory     |
| OpenAI, ...   |    +-------------------+
+---------------+
       |                       |
       +----------+------------+
                  |
         +--------v--------+      +--------------------+
         | Victor.Models   |<-----| Victor.Migrator    |  EF migrations,
         | (data layer)    |      | (migration runner) |  IDesignTimeDbContextFactory,
         | EF entities,    |      | dotnet ef / k8s    |  Program.cs (DATABASE_URL)
         | VictorDbContext |      +--------------------+
         +-----------------+
```

## Victor.Models (`src/Victor.Models/`)

Thin data layer. EF Core entities and `VictorDbContext` live here.
No business logic, no migrations. Referenced by Victor.Core, Victor.Migrator, and any project that needs DB access.
Package versions centralized via `Directory.Packages.props`.

```csharp
Job                                           // EF entity: Id, Description, RequestedBy, ChannelId, ThreadTs, Status (Queued|Running|Completed|Failed|Cancelled), CurrentPhase, LastStatusMessage, Result, Error, timestamps
MemoryRecord                                  // EF entity: Id, Timestamp, TaskId, Category, Summary, Embedding (vector)
VictorDbContext                               // DbSet<Job>, DbSet<MemoryRecord>, pgvector extension
```

Registered via `IDbContextFactory<VictorDbContext>` in Victor.Host.

---

## Victor.Migrator (`src/Victor.Migrator/`)

Dedicated migration runner. Executable project — used locally for `dotnet ef` commands and
as a dedicated init-container in Kubernetes for applying migrations before the main pod starts.

```csharp
VictorDbContextFactory : IDesignTimeDbContextFactory<VictorDbContext>  // design-time only; reads DATABASE_URL or localhost fallback
Migrations/                                   // EF migrations generated against Victor.Models.VictorDbContext
Program.cs                                    // reads DATABASE_URL env var, calls Database.MigrateAsync(), exits
```

To add a migration (run from repo root or from `src/Victor.Migrator/`):
```
cd src/Victor.Migrator
dotnet ef migrations add <Name>
```

To apply migrations locally:
```
DATABASE_URL="Host=localhost;..." dotnet run --project src/Victor.Migrator
```

---

## Victor.Core (`src/Victor.Core/`)

Central library. References Victor.Models for DB types.

### Abstractions

```csharp
interface ILLMProvider          // CompleteAsync, StreamAsync
interface IEmbeddingProvider    // GetEmbeddingAsync (provider-agnostic)
interface ITool                 // Name, Description, InputSchema, ExecuteAsync
interface IApprovalGateway      // RequestApprovalAsync (for safety-gated commands)
interface IMessageClassifier        // NeedsHistoryAsync — cheap triage model classifies standalone vs follow-up
interface IConversationHistoryProvider // GetHistoryAsync — on-demand history fetch, impl by SlackHistoryService
interface IUserQueryGateway    // AskAsync — posts free-form question, waits for user's text reply
interface IJobStatusNotifier    // NotifyPhaseStarted (no-op), NotifyPhaseCompleted, NotifyJobCompleted/Failed
```

### Domain Models

| Record | Purpose |
|--------|---------|
| `Message(Role, Content)` | Conversation turn |
| `LLMRequest(SystemPrompt, Messages, Tools?, MaxTokens)` | Request to any LLM |
| `LLMResponse(Content, StopReason, ToolUses?)` | LLM reply |
| `ToolUse(Id, ToolName, Input)` | LLM-requested tool call |
| `ToolResult(ToolUseId, Output, IsError)` | Tool execution result |

EF entities (`Job`, `MemoryRecord`) live in **Victor.Models**.

### Orchestration

```csharp
[RegisterSingleton] Orchestrator              // IOptions<OrchestratorOptions>, ILLMProvider, IEnumerable<ITool>, IApprovalGateway, JobQueue
                                              // Writes Job.CurrentPhase + LastStatusMessage at phase transitions and tool calls
                                              // Sets AskUserTool.CurrentJob before each run so ask_user has channel context
[RegisterSingleton] AskUserTool : ITool       // Posts question to Slack via IUserQueryGateway, blocks until user replies (10min timeout)
                                              // Available in all orchestration phases (Research, Planning, Execution)
OrchestratorOptions                           // SystemPrompt, Research/Planning/Execution PhaseConfigs (section "orchestration")
PhaseConfig                                   // Type, AllowedTools, ApprovalTimeout, SafetyPatterns
```

```
Job arrives via JobQueue
  -> JobProcessorService (BackgroundService, reads channel)
       -> Registers per-job CancellationTokenSource (linked to host stopping token)
       -> Orchestrator.RunAsync(job, conversationHistory?, jobCt)
            -> Phase: Research   (read-only tools, gather context)
            -> Phase: Planning   (reason about approach, produce plan)
            -> Phase: Execution  (mutating tools, carry out plan)
       -> On OperationCanceledException (job ct, not host): marks Cancelled
       -> Unregisters CTS on completion
```

Each phase: LLM loop with tool calls filtered by `PhaseConfig.AllowedTools`.
Commands matching `PhaseConfig.SafetyPatterns` go through `IApprovalGateway`.

**Job cancellation:** `JobQueue` tracks a `ConcurrentDictionary<Guid, CTS>` for running jobs.
`CancelJobAsync(id)` signals running jobs or directly marks queued jobs as `Cancelled`.

### Conversation (Tier 1 — synchronous)

```csharp
[RegisterSingleton] ConversationHandler          // LLM loop with read-only tool whitelist + start_job pseudo-tool,
                                                  // iteration cap, active-job context injection (phase + status), uses OrchestratorOptions.SystemPrompt
                                                  // Receives either single message (standalone) or full history (follow-up, per triage)
ConversationHandlerOptions                        // MaxToolIterations, AllowedTools (section "conversation")
[RegisterSingleton] ConversationQueue             // ConcurrentDictionary<string, SemaphoreSlim> — per-conversation serialization
[RegisterSingleton] QueryJobStatusTool : ITool    // Queries Job by ID or lists active jobs (status, phase, last update)
[RegisterSingleton] CancelJobTool : ITool         // Cancels a running or queued job by ID via JobQueue.CancelJobAsync
```

```
Slack message arrives
  -> SlackListenerService checks for pending ask_user query → route reply if found
  -> Otherwise:
       -> IMessageClassifier.NeedsHistoryAsync (cheap/fast triage model, e.g. gpt-4o-mini)
            -> STANDALONE: pass only the current message
            -> FOLLOW_UP: fetch history via SlackHistoryService, pass full context
       -> ConversationQueue.ExecuteAsync(conversation_id, ...)
            -> ConversationHandler.HandleAsync(messages, userId)
                 -> LLM decides: text reply / tool call / start_job / cancel_job
                 -> If start_job: enqueues to JobQueue (Tier 2)
                 -> If cancel_job: signals cancellation via JobQueue
            -> SlackNotifier posts reply
```

### JobQueue (Tier 2 — asynchronous)

```csharp
[RegisterSingleton] JobQueue                  // Channel<Guid> + EF persistence via IDbContextFactory
                                              // + ConcurrentDictionary<Guid, IReadOnlyList<Message>> for conversation context
[RegisterSingleton] JobProcessorService : BackgroundService  // Reads queue, drives Orchestrator, persists job status
```

---

## Victor.Providers.OpenAI (`src/Victor.Providers.OpenAI/`)

Implements `ILLMProvider` against the OpenAI Chat Completions API.
Uses `OpenAI` NuGet v2.2. Translates `LLMRequest` <-> OpenAI models.

```csharp
OpenAIOptions                                   // ApiKey, Model, TriageModel, EmbeddingModel, EmbeddingDimensions (section "llm:openai")
[RegisterSingleton] OpenAILLMProvider : ILLMProvider        // CompleteAsync maps tool calls, StreamAsync yields text
[RegisterSingleton] OpenAIMessageClassifier : IMessageClassifier  // Uses TriageModel (cheap) to classify standalone vs follow-up
[RegisterSingleton] OpenAIEmbeddingProvider : IEmbeddingProvider  // text-embedding-3-small, configurable dimensions
```

---

## Victor.Tools.Shell (`src/Victor.Tools.Shell/`)

Executes shell commands inside Victor's container.

```csharp
ShellOptions                                     // ShellPath, WorkingDirectory, AuditLogDirectory, TimeoutSeconds, SafetyPatterns
[RegisterSingleton] ShellExecTool : ITool         // Runs Process, returns stdout/stderr/exit_code, audit-logs via AuditLogger
[RegisterSingleton] SafetyInterceptor             // Checks command against ShellOptions.SafetyPatterns
[RegisterSingleton] AuditLogger                   // Appends to /workspace/task_logs/{jobId}.log
```

Input schema: `{ "command": "string" }`. Timeout kills process tree.

---

## Victor.Tools.AzureKeyVault (`src/Victor.Tools.AzureKeyVault/`)

Dedicated ITool for reading and writing Azure Key Vault secrets. Victor must use this
instead of shell commands for all secret operations — cleaner audit trail, works
transparently with pod Workload Identity, and benefits from in-process caching.

```csharp
AzureKeyVaultOptions                              // VaultUri, CacheTtlSeconds (section "tools:keyvault")
[RegisterSingleton(Type = typeof(ITool))]
AzureKeyVaultTool : ITool                         // actions: get | set | list | delete
                                                  // SecretClient via DefaultAzureCredential
                                                  // ConcurrentDictionary cache with TTL (get/set write-through)
```

Input schema: `{ "action": "get|set|list|delete", "name": "string", "value": "string (set only)" }`
`list` returns enabled secret names only (no values).
`delete` initiates Key Vault soft-delete.
NuGet: `Azure.Security.KeyVault.Secrets`, `Azure.Identity`

---

## Victor.Tools.Memory (`src/Victor.Tools.Memory/`)

Uses `IEmbeddingProvider` (provider-agnostic) and shared `VictorDbContext` from Core.

```csharp
MemoryOptions                                    // RecallTopK (section "tools:memory")
[RegisterScoped] MemoryStore                      // Uses VictorDbContext: StoreAsync, RecallAsync (cosine distance), DeleteAsync, UpdateAsync
[RegisterScoped] MemoryTool : ITool               // action: "store" | "recall" | "delete" | "update", uses IEmbeddingProvider + MemoryStore
                                                   // recall returns IDs; delete removes by ID; update rewrites text + re-embeds
```

## Victor.Slack (`src/Victor.Slack/`)

Slack integration via SlackNet (Web API) + raw WebSocket Socket Mode.
`SlackServiceExtensions.AddVictorSlack(config)` registers `ISlackApiClient`
and `IHttpClientFactory` (can't use Firefly for third-party library wiring).

```csharp
SlackOptions                                     // BotToken, AppToken, DefaultChannelId, HistoryMessageCount (section "slack")
[RegisterSingleton] SlackNotifier                 // PostMessageAsync (threads), UpdateMessageAsync, SetPresenceAsync
[RegisterSingleton] SlackApprovalGateway : IApprovalGateway  // Posts approve/reject buttons, waits via ConcurrentDictionary<TCS>
[RegisterSingleton] SlackUserQueryGateway : IUserQueryGateway // Posts question text, waits for free-form user reply via TCS
                                                   // SlackListenerService routes replies to pending queries before ConversationHandler
[RegisterSingleton] SlackHistoryService            // Fetches conversations.history / conversations.replies,
                                                   // resolves bot user ID via auth.test, caches user display names,
                                                   // maps Slack messages to Message(Role, Content) with timestamps
                                                   // Format: "[YYYY-MM-DD HH:mm from @user] text" / "[YYYY-MM-DD HH:mm] text"
                                                   // Filters out automated status messages by pattern matching (old hardcoded + prefixed)
[RegisterSingleton] SlackJobStatusNotifier : IJobStatusNotifier  // Phase-start: no-op (log only, no Slack message)
                                                   // Phase-completed: posts LLM-generated plan summary
                                                   // Job completed/failed: posts LLM result or natural error message
[RegisterSingleton] SlackListenerService : IHostedService    // Socket Mode via raw WebSocket, routes messages through:
                                                   // 1. Pending ask_user queries (SlackUserQueryGateway.HandleReply)
                                                   // 2. ConversationQueue → ConversationHandler (normal flow)
                                                   // 3. Button clicks → SlackApprovalGateway
                                                   // Replies in main channel for channel msgs, in-thread for threaded msgs
SlackServiceExtensions.AddVictorSlack(config)     // ISlackApiClient + IHttpClientFactory + options binding
```

## Victor.Host (`src/Victor.Host/`)

ASP.NET Core Web host. Entry point binary (`victor`).

```
Program.cs
  AddYamlFile(config.yaml)          // /config/config.yaml (k8s) or local config.yaml (dev)
  AddAzureKeyVault(...)              // optional; skipped when keyVault:endpoint is empty
  UseSerilog(...)                    // structured console logging, config-driven
  AddFireflyServiceRegistration(b => b.UseAssembly(...)) // scans all project assemblies (incl. Victor.Models)
  AddVictorSlack(config)             // SlackNet manual wiring
  AddDbContextFactory<VictorDbContext>(UseNpgsql + UseVector)
  Configure<ConversationHandlerOptions> // section "conversation"
  Configure<OrchestratorOptions>     // section "orchestration"
  PostConfigure<OrchestratorOptions> // reads orchestration:systemPromptFile into SystemPrompt
  Configure<OpenAIOptions>           // section "llm:openai"
  Configure<ShellOptions>            // section "tools:shell"
  Configure<MemoryOptions>           // section "tools:memory"
  AddHealthChecks().AddDbContextCheck<VictorDbContext>(tags: ["ready"])
  MapHealthChecks("/healthz/live")   // always 200 (no checks)
  MapHealthChecks("/healthz/ready")  // DB connectivity check
```

NuGet: `Serilog.AspNetCore`, `NetEscapades.Configuration.Yaml`,
`Azure.Identity`, `Azure.Extensions.AspNetCore.Configuration.Secrets`,
`Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore`

**Persona / system prompt loading:**
`orchestration:systemPromptFile` → path to a `.md` file read at startup.
`orchestration:systemPrompt` → inline fallback (used in local dev without a mounted file).
Infrastructure provides the file (k8s ConfigMap mounted at `/config/persona.md`).

Config file locations:
- **k8s**: `/config/config.yaml` (mounted from ConfigMap `victor-config`)
- **dev**: `config.yaml` next to the binary (`src/Victor.Host/config.yaml` → copied to output)

---

## Deployment (`deploy/`)

### `deploy/docker/Dockerfile`

Multi-stage build. Build image: `mcr.microsoft.com/dotnet/sdk:10.0`.
Runtime image: `mcr.microsoft.com/dotnet/aspnet:10.0`. Binary name: `victor`.
`/config` and `/workspace` are volume mount points — not baked into the image.

### `deploy/k8s/`

| File | Purpose |
|------|---------|
| `namespace.yaml` | `victor` namespace |
| `serviceaccount.yaml` | SA with `azure.workload.identity/client-id` annotation |
| `configmap.yaml` | `victor-config` ConfigMap with `config.yaml` + `persona.md` keys |
| `deployment.yaml` | Single-replica Deployment; mounts ConfigMap to `/config`; liveness + readiness probes |

Workload Identity: pod label `azure.workload.identity/use: "true"` + SA annotation.
Secrets flow: Key Vault endpoint set in ConfigMap → Azure SDK injects secrets at startup.
