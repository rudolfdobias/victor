# Victor — Project Context

Victor is a Kubernetes-deployable AI agent written in C# / .NET 10.
It is a framework for virtual AI employees. The first persona is a
senior DevOps engineer.

## Repository Structure

    /victor
      /src
        /Victor.Core                  # ILLMProvider, ITool, Orchestrator,
                                      # phase engine, job queue
        /Victor.Providers.OpenAI      # OpenAI API implementation (first)
        /Victor.Providers.Anthropic   # Anthropic API implementation
        /Victor.Slack                 # Slack bot, event handling,
                                      # status and presence management
        /Victor.Tools.Shell           # ShellExecTool, safety interceptor,
                                      # audit logger
        /Victor.Tools.Memory          # Vector memory via pgvector
        /Victor.Persona.DevOps        # DevOps system prompt loaded
                                      # at runtime from .md file
        /Victor.Host                  # ASP.NET Core host, DI, config,
                                      # health checks
      /deploy
        /k8s                          # Kubernetes manifests
        /docker                       # Dockerfile
      /config
        config.yaml                   # all configuration, documented

## Architecture

See `.ai/ARCHITECTURE.md` for the full component map and how classes
connect. **Update that file every time a component is added, modified,
or removed** — this is a standing rule, not a per-request instruction.

## Key Architectural Decisions

**LLM:** Abstracted via ILLMProvider. First implementation: OpenAI.
Selected via config.yaml. Rest of system is provider-agnostic.

**Configuration:** YAML (`config.yaml`). No JSON config files.

**Orchestration:** Three fixed phases — Research, Planning, Execution.
Enforced in C#. LLM drives reasoning within each phase.
Phase config (tools available, approval timeout, safety patterns)
lives in config.yaml.

**Tool execution:** Primary tool is ShellExecTool — executes shell
commands in Victor's container, returns stdout/stderr/exit code.
Victor uses CLI tools (kubectl, helm, flux, az, git, trivy etc.)
natively. No per-CLI wrappers.

**Memory:** pgvector (PostgreSQL + pgvector extension, Azure hosted).
Embeddings via OpenAI text-embedding-3-small regardless of LLM provider.
Memory written by Victor after each task. Retrieved via cosine similarity
at task start. Schema: id, timestamp, task_id, category, summary, embedding.

**Safety:** Configurable shell command patterns require Slack approval
before execution. All shell commands audit-logged to /workspace/task_logs/.

**Secrets:** Azure Key Vault via Workload Identity. No secrets in config
except Key Vault endpoint.

**Communication:** Slack via Slack Bolt. Victor responds in threads,
updates status dynamically, communicates in first person like an engineer.

**Scheduling:** IHostedService with cron expressions in config.yaml.

## Conventions

- All async, CancellationToken everywhere
- Serilog for structured logging
- Firefly.DependencyInjection for DI registration (attributes, not manual extensions)
- Microsoft.Extensions.* for Configuration, Hosting, Options
- Health endpoints: /healthz/live and /healthz/ready
- No hardcoded strings

## Agent Memory

Conversation memory is stored in `.ai/MEMORY.md`. Read it at the start of each
session and update it when learning new preferences, feedback, or project state.
Keep entries brief (1-2 lines)
Also update PROJECT.md with changes and new features that will come along.

## Current Status

[UPDATE THIS SECTION as the project progresses]
- [x] Victor.Core interfaces
- [x] Victor.Providers.OpenAI
- [x] Victor.Tools.Shell
- [x] Orchestrator loop
- [x] Victor.Slack
- [x] Victor.Tools.Memory (pgvector)
- [SKIP] Victor.Providers.Anthropic
- [SKIP] Victor.Persona.DevOps (replaced by file-based persona via config)
- [x] Victor.Host
- [x] Dockerfile + k8s manifests
