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
```

## Victor.Core (`src/Victor.Core/`)

Central library. EF Core + Npgsql + pgvector for shared `VictorDbContext`.
Package versions centralized via `Directory.Packages.props`.

### Abstractions

```csharp
interface ILLMProvider          // CompleteAsync, StreamAsync
interface IEmbeddingProvider    // GetEmbeddingAsync (provider-agnostic)
interface ITool                 // Name, Description, InputSchema, ExecuteAsync
interface IApprovalGateway      // RequestApprovalAsync (for safety-gated commands)
```

### Models

| Record | Purpose |
|--------|---------|
| `Message(Role, Content)` | Conversation turn |
| `LLMRequest(SystemPrompt, Messages, Tools?, MaxTokens)` | Request to any LLM |
| `LLMResponse(Content, StopReason, ToolUses?)` | LLM reply |
| `ToolUse(Id, ToolName, Input)` | LLM-requested tool call |
| `ToolResult(ToolUseId, Output, IsError)` | Tool execution result |
| `Job` | EF entity: Id, Description, RequestedBy, Status, Result, Error, timestamps |
| `MemoryRecord` | EF entity: Id, Timestamp, TaskId, Category, Summary, Embedding (vector) |

### Orchestration

```csharp
[RegisterSingleton] Orchestrator              // IOptions<OrchestratorOptions>, ILLMProvider, IEnumerable<ITool>, IApprovalGateway
OrchestratorOptions                           // SystemPrompt, Research/Planning/Execution PhaseConfigs (section "orchestration")
PhaseConfig                                   // Type, AllowedTools, ApprovalTimeout, SafetyPatterns
```

```
Job arrives via JobQueue
  -> JobProcessorService (BackgroundService, reads channel)
       -> Orchestrator.RunAsync(job)
            -> Phase: Research   (read-only tools, gather context)
            -> Phase: Planning   (reason about approach, produce plan)
            -> Phase: Execution  (mutating tools, carry out plan)
```

Each phase: LLM loop with tool calls filtered by `PhaseConfig.AllowedTools`.
Commands matching `PhaseConfig.SafetyPatterns` go through `IApprovalGateway`.

### Data

```csharp
VictorDbContext                               // DbSet<Job>, DbSet<MemoryRecord>, pgvector extension
```

Shared across the entire app. Registered via `IDbContextFactory<VictorDbContext>`.

### JobQueue

```csharp
[RegisterSingleton] JobQueue                  // Channel<Guid> + EF persistence via IDbContextFactory
[RegisterSingleton] JobProcessorService : BackgroundService  // Reads queue, drives Orchestrator, persists job status
```

---

## Victor.Providers.OpenAI (`src/Victor.Providers.OpenAI/`)

Implements `ILLMProvider` against the OpenAI Chat Completions API.
Uses `OpenAI` NuGet v2.2. Translates `LLMRequest` <-> OpenAI models.

```csharp
OpenAIOptions                                   // ApiKey, Model, EmbeddingModel, EmbeddingDimensions (section "llm:openai")
[RegisterSingleton] OpenAILLMProvider : ILLMProvider        // CompleteAsync maps tool calls, StreamAsync yields text
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

## Victor.Tools.Memory (`src/Victor.Tools.Memory/`)

Uses `IEmbeddingProvider` (provider-agnostic) and shared `VictorDbContext` from Core.

```csharp
MemoryOptions                                    // RecallTopK (section "tools:memory")
[RegisterScoped] MemoryStore                      // Uses VictorDbContext: StoreAsync, RecallAsync (cosine distance)
[RegisterScoped] MemoryTool : ITool               // action: "store" | "recall", uses IEmbeddingProvider + MemoryStore
```

## Victor.Slack (`src/Victor.Slack/`)

Slack integration via SlackNet (Web API) + raw WebSocket Socket Mode.
`SlackServiceExtensions.AddVictorSlack(config)` registers `ISlackApiClient`
and `IHttpClientFactory` (can't use Firefly for third-party library wiring).

```csharp
SlackOptions                                     // BotToken, AppToken, DefaultChannelId (section "slack")
[RegisterSingleton] SlackNotifier                 // PostMessageAsync (threads), UpdateMessageAsync, SetPresenceAsync
[RegisterSingleton] SlackApprovalGateway : IApprovalGateway  // Posts approve/reject buttons, waits via ConcurrentDictionary<TCS>
[RegisterSingleton] SlackListenerService : IHostedService    // Socket Mode via raw WebSocket, enqueues jobs, routes button clicks
SlackServiceExtensions.AddVictorSlack(config)     // ISlackApiClient + IHttpClientFactory + options binding
```

## Victor.Host (`src/Victor.Host/`)

ASP.NET Core Web host. Entry point binary (`victor`).

```
Program.cs
  AddYamlFile(config.yaml)          // /config/config.yaml (k8s) or local config.yaml (dev)
  AddAzureKeyVault(...)              // optional; skipped when keyVault:endpoint is empty
  UseSerilog(...)                    // structured console logging, config-driven
  AddFireflyServiceRegistration(b => b.UseAssembly(...)) // scans all project assemblies
  AddVictorSlack(config)             // SlackNet manual wiring
  AddDbContextFactory<VictorDbContext>(UseNpgsql + UseVector)
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
