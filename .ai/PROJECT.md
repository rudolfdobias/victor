# Victor — Project Context

Victor is a Kubernetes-deployable AI agent written in C# / .NET 10. It is a
framework for **virtual AI employees**. The first persona is a senior DevOps
engineer.

The defining property of Victor is that he behaves like a coworker, not a
command-line tool: most messages he handles are **conversational** (fast,
contextual, synchronous), and only a subset are **long-running jobs**
(queued, multi-phase, fire-and-forget with status updates). He runs both
modes concurrently and independently.

## Repository Structure

    /victor
      /src
        /Victor.Models                # EF entities (Job, MemoryRecord),
                                      # VictorDbContext, EF migrations
        /Victor.Migrator              # Dedicated migration runner
                                      # (executable; init-container in k8s)
        /Victor.Core                  # ILLMProvider, ITool, ConversationHandler,
                                      # Orchestrator, phase engine, queues
        /Victor.Providers.OpenAI      # OpenAI API implementation (first)
        /Victor.Slack                 # Slack bot, event handling,
                                      # status and presence management,
                                      # Slack history retrieval
        /Victor.Tools.Shell           # ShellExecTool, safety interceptor,
                                      # audit logger
        /Victor.Tools.AzureKeyVault   # Key Vault read/write tool
        /Victor.Tools.Memory          # Vector memory via pgvector
        /Victor.Host                  # ASP.NET Core host, DI, config,
                                      # health checks
      /deploy
        /k8s                          # Kubernetes manifests
        /docker                       # Dockerfile
      /config
        config.yaml                   # all configuration, documented
        persona.md                    # system prompt, mounted at runtime

See `.ai/ARCHITECTURE.md` for the full component map and how classes connect.
**Update that file every time a component is added, modified, or removed** —
this is a standing rule, not a per-request instruction.

See `.ai/MEMORY.md` for the full agent memory and how it's updated. Use this file as your memory. Use some compact 
format of writing / markup and update the memory with every eliglible information about the project. 

---

## Two-Tier Execution Model

This is the core architectural shift away from "every Slack message is a Job".
Victor has two cooperating subsystems:

### Tier 1 — ConversationHandler (synchronous, per-conversation)

Every inbound Slack message hits ConversationHandler first. It runs an LLM
turn with:

- The last N Slack messages from the conversation as chat history
- A summary of the user's currently active jobs injected as system context
- A tightly scoped, **read-only / safe** tool whitelist
- A hard cap on tool-call iterations (e.g. 5)

The LLM decides what kind of message it is by what it does:

- Pure text reply → answer directly
- Calls a read-only tool (wiki search, `kubectl get`, status query) → answer with what it found
- Calls `start_job(...)` → enqueue a job, reply with an acknowledgement, and let the job run async

ConversationHandler **never mutates infrastructure**. Anything that changes
state goes through the job pipeline.

### Tier 2 — Orchestrator + JobQueue (asynchronous, system-wide serial)

When ConversationHandler invokes `start_job`, the description is enqueued to
the JobQueue. JobWorker (BackgroundService) drains the queue with
**degree-of-parallelism = 1** and runs each job through the Orchestrator's
three fixed phases — **Research → Planning → Execution** — exactly as before.
Phase config (allowed tools, approval timeout, safety patterns) lives in
`config.yaml`.

JobWorker writes mid-flight state (`CurrentPhase`, `LastStatusMessage`) to the
`Job` row as it progresses, and posts thread updates to Slack at phase
transitions. ConversationHandler reads this state live, which is what allows
Victor to answer "is it done?" while a job is in flight.

### Per-conversation queue

Two messages from the same conversation cannot be processed concurrently
(race conditions, contradictory replies, double-starting jobs). Victor has a
keyed serialization layer in front of ConversationHandler:

- Key = `conversation_id`:
    - DM: the IM channel id
    - Threaded mention: `channel_id + thread_ts`
    - Channel mention without a thread: `channel_id` (rare; acceptable to lock on channel)
- Implementation: `ConcurrentDictionary<string, SemaphoreSlim>`. Acquire per
  message, release on completion. Idle entries can be evicted lazily.
- Different conversations run in parallel. Same conversation runs serial.

This is a **separate queue from JobQueue** with opposite design goals:
ConversationQueue wants per-key serial + cross-key parallel + low latency;
JobQueue wants system-wide serial + minutes-to-hours latency. Sharing them
recreates the bug this design exists to fix.

### Behavioural examples

| User says | Victor's path | Reply |
|-----------|---------------|-------|
| "What's your name?" | ConversationHandler, no tools | "Victor." |
| "Hey, are you there?" | ConversationHandler, no tools | "Yeah, what's up?" |
| "How do I write a retry rule in Reactor?" | ConversationHandler, calls `wiki_search` 1–2× | Inline answer with what was found |
| "Which API do you mean?" (clarifying) | ConversationHandler, no tools | "Core API or Reactor API?" |
| "Is the API on staging ok?" | ConversationHandler, calls read-only `kubectl get` | "Looks fine." / "It's crashing — missing `XXX` in config, tell the backend team." |
| "Is that deploy done yet?" (during a running job) | ConversationHandler reads active-job context | "Not yet, currently in Execution phase, last update: provisioning node pool." |
| "Add 4 D8 nodes to staging in spot mode for core importer" | ConversationHandler calls `start_job(...)` | "On it, I'll post updates in this thread." Then JobWorker takes over. |

### Message triage & history loading

A cheap triage model (`IMessageClassifier`, default `gpt-4o-mini`) classifies
every incoming message as **STANDALONE** or **FOLLOW_UP** before the main LLM
sees it. Only follow-ups trigger a history fetch.

- STANDALONE ("restart the API", "save this key"): only the current message is
  passed to ConversationHandler.
- FOLLOW_UP ("try it again", "yes do it"): `SlackHistoryService` fetches
  conversation history, which is passed alongside the current message.

When history is loaded:
- Threaded: `conversations.replies` for the thread
- Channel/DM: `conversations.history`, last N messages (~20)

Each Slack message maps to `Message(Role, Content)`:
- Victor's own messages → `Role.Assistant`, prefixed with `[YYYY-MM-DD HH:mm]`
- Everyone else → `Role.User`, prefixed with `[YYYY-MM-DD HH:mm from @username]`

Timestamps let the LLM distinguish Monday's conversation from Tuesday's and
focus on the most recent request instead of re-processing old ones.

Automated job status messages (phase-start notifications, "Job failed:",
bare "Done.") are **filtered out** by pattern matching in `SlackHistoryService`.
This prevents the LLM from mimicking templated bot messages instead of
responding naturally.

Job tool-result turns are **not** replayed into history. The LLM sees what
the user saw — same as a human re-reading the thread.

Active jobs for the current user are injected as a system message, e.g.:

    Active jobs:
    - job abc123 — "Add D8 nodes to staging" — phase: Execution
      last update: "Provisioning node pool"

### Tool boundaries

| Tool | Available to ConversationHandler | Available to Orchestrator |
|------|----------------------------------|---------------------------|
| `wiki_search`, doc lookup | Yes | Yes (Research phase) |
| `kubectl get`, read-only queries | Yes | Yes (Research phase) |
| `query_job_status` | Yes | — |
| `cancel_job` | Yes | — |
| `start_job` | Yes | — |
| `ask_user` | — | Yes (all phases, posts question to Slack, waits for reply) |
| `kubectl apply`, `helm upgrade`, mutating ops | **No** | Yes (Execution phase, behind safety gateway) |
| `MemoryTool` recall | Yes | Yes |
| `MemoryTool` store | No | Yes (post-job) |
| `MemoryTool` delete/update | No | Yes |
| Key Vault `set` / `delete` | No | Yes (Execution phase, behind safety gateway) |

Mutation always goes through Execution phase and `IApprovalGateway`.

---

## Key Architectural Decisions

**LLM:** Abstracted via `ILLMProvider`. First implementation: OpenAI. Selected
via `config.yaml`. The rest of the system stays provider-agnostic. A separate
cheap triage model (`IMessageClassifier`, default `gpt-4o-mini`, configured via
`llm:openai:triageModel`) classifies incoming messages as standalone vs follow-up
before the main model processes them.

**Configuration:** YAML (`config.yaml`). No JSON config files.

**Conversation:** Stateless ConversationHandler invoked per Slack message,
serialized per conversation_id, fed Slack history + active job context.

**Orchestration:** Three fixed phases — Research, Planning, Execution.
Enforced in C#. LLM drives reasoning within each phase. Used only for jobs
started via `start_job`.

**Job concurrency:** JobWorker runs at parallelism = 1 initially. Additional
jobs queue. ConversationHandler can report queue position.

**Job cancellation:** Supported. `CancelJobTool` (available in conversation mode)
calls `JobQueue.CancelJobAsync`, which signals the per-job `CancellationTokenSource`
for running jobs or directly marks queued jobs as `Cancelled`. The cancellation
propagates through the CT chain into Orchestrator, LLM calls, and tool execution.
Mid-flight redirection and partial-state recovery are not supported.

**Tool execution:** Primary tool is `ShellExecTool` — executes shell commands
in Victor's container and returns stdout/stderr/exit code. Victor uses CLI
tools (kubectl, helm, flux, az, git, trivy, etc.) natively. No per-CLI
wrappers. Exception: secret operations go through `Victor.Tools.AzureKeyVault`
for audit trail and Workload Identity integration.

**Memory:** pgvector (PostgreSQL + pgvector extension, Azure-hosted).
Embeddings via OpenAI `text-embedding-3-small` regardless of LLM provider.
Memory written by Victor at job completion. Retrieved via cosine similarity
at job start and accessible to ConversationHandler for recall. Schema: id,
timestamp, task_id, category, summary, embedding.

**Safety:** Configurable shell command patterns require Slack approval before
execution (Execution phase only). All shell commands audit-logged to
`/workspace/task_logs/{jobId}.log`. ConversationHandler has no access to
mutating tools, so safety patterns are not in its path.

**Secrets:** Azure Key Vault via Workload Identity. No secrets in config
except the Key Vault endpoint. Victor uses `Victor.Tools.AzureKeyVault` for
all secret reads/writes — never shell.

**Communication:** Slack via SlackNet (Web API) + raw WebSocket Socket Mode.
Victor replies in the main channel for channel messages, and stays in threads
for threaded messages. No hardcoded status messages — all user-facing text is
LLM-generated. Job status notifications are minimal: plan summary (LLM output),
final result (LLM output), or natural error message. Phase-start notifications
are log-only (no Slack message). The `ask_user` tool lets the Orchestrator
post questions to Slack mid-job and block until the user replies (10min timeout).
`SlackListenerService` routes replies to pending queries before ConversationHandler.

**Scheduling:** `IHostedService` with cron expressions in `config.yaml`.

---

## Conventions

- All async, `CancellationToken` everywhere
- Serilog for structured logging
- **Firefly.DependencyInjection** for DI registration (attributes:
  `[RegisterSingleton]`, `[RegisterScoped]`, `[RegisterTransient]`).
  No manual `IServiceCollection` extensions.
- `Microsoft.Extensions.*` for Configuration, Hosting, Options
- EF Core + migrations for database access; no raw SQL
- DbContext at app-wide level (`VictorDbContext` lives in `Victor.Models`,
  registered via `IDbContextFactory` in `Victor.Host`)
- `Directory.Packages.props` for centralized NuGet version management
- Health endpoints: `/healthz/live` (always 200) and `/healthz/ready` (DB check)
- No hardcoded strings — everything to config or constants
- Provider-agnostic core abstractions (`IEmbeddingProvider`, not OpenAI-specific)
- **Every executable project MUST link `/config/config.yaml` under
  `ConfigFiles\config.yaml` in its `.csproj`** (`CopyToOutputDirectory:
  PreserveNewest`). Currently: `Victor.Host` and `Victor.Migrator`.
  Local path MUST use `Path.Combine(AppContext.BaseDirectory,
  "ConfigFiles/config.yaml")` — NOT a bare relative path — because
  `dotnet run` sets CWD to the project source dir, not the bin output.
- Persona / system prompt lives in `persona.md`, mounted from a ConfigMap at
  `/config/persona.md` in k8s and read via `orchestration:systemPromptFile`.
  Inline `orchestration:systemPrompt` is the dev fallback.

---

## Agent Memory

> **IMPORTANT: `.ai/MEMORY.md` MUST be kept up to date.** Every time a
> non-obvious convention, constraint, or architectural decision is
> established or corrected, add it to MEMORY.md immediately. Failing to do so
> causes the same mistakes to recur across sessions (e.g. forgetting to link
> `config.yaml` in new executable projects). When in doubt, write it down.

Conversation memory is stored in `.ai/MEMORY.md`. Read it at the start of each
session and update it when learning new preferences, feedback, or project
state. Keep entries brief (1–2 lines).

Also update `PROJECT.md` (this file) with structural changes and new features
as they land. `ARCHITECTURE.md` tracks the component map.

---

## Build Order — Conversation/Job Split

The architectural shift is shippable incrementally. Each step is independently
useful.

1. **Slack history fetch.** Add `conversations.history` / `conversations.replies`
   retrieval and pass the last N messages into the LLM call. Immediately makes
   Victor stop being amnesiac, even before the bigger refactor.

2. **Extract ConversationHandler.** New service in `Victor.Core`. Move Slack
   ingest to call it instead of enqueueing directly. Give it a read-only tool
   set + a `start_job` tool that enqueues to the existing `JobQueue`. Add the
   per-conversation `SemaphoreSlim` keyed by `conversation_id`.

3. **Live job state.** Add `Job.CurrentPhase` and `Job.LastStatusMessage`,
   updated by Orchestrator at phase transitions and after meaningful tool
   calls. Add `query_job_status` tool. Inject active-job summary into
   ConversationHandler's system context.

4. **Thread status updates from JobWorker.** JobWorker posts mid-flight Slack
   thread replies as phases transition ("Done with research, here's the
   plan…", "Applying changes…", "Done.").

---

## Known Follow-ups (Out of Scope for v1)

- **Coalescing rapid-fire messages.** When three messages arrive within a few
  seconds on the same conversation, drain the mailbox and process them as one
  turn instead of three sequential LLM calls. Slack history fetch already
  gives the LLM everything; this is a cost optimization.
- **Job mid-flight redirection.** Jobs can be cancelled but not redirected
  mid-flight. The user must cancel and start a new job with updated instructions.
- **Multi-replica JobWorker.** Single-replica is correct while audit-log and
  workspace assumptions assume one execution slot. Revisit when load justifies.