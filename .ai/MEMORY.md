# Memory

Agent memory for this project. Keep entries brief — one or two lines each.

## User
- Rudolf is the developer on this project.
- Rudolf is also the author of Firefly.DependencyInjection.

## Feedback
- First LLM provider to implement is OpenAI, not Anthropic.
- Config files must be YAML, never JSON.
- Always update `.ai/ARCHITECTURE.md` when adding/modifying/deleting a component — standing rule.
- Use Firefly.DependencyInjection for DI (attributes), not manual ServiceCollectionExtensions.
- Keep core abstractions provider-agnostic (e.g. IEmbeddingProvider, not hardwired OpenAI).
- Use EF Core + migrations for database access, not raw SQL.
- DbContext belongs at app-wide level (Victor.Core), not per-module. User plans to persist more state (e.g. jobs).
- Use Directory.Packages.props for centralized NuGet version management.
- **Every executable project MUST link `/config/config.yaml` under `ConfigFiles\config.yaml` in its .csproj** (CopyToOutputDirectory: PreserveNewest). Currently: Victor.Host and Victor.Migrator. Local path MUST use `Path.Combine(AppContext.BaseDirectory, "ConfigFiles/config.yaml")` — NOT a bare relative path — because `dotnet run` sets CWD to the project source dir, not the bin output.

## Project State
- Done: Victor.Core, Providers.OpenAI, Tools.Shell, Orchestrator loop, Victor.Slack, Tools.Memory.
- Anthropic provider skipped by user.
- Next: Victor.Host, then Dockerfile + k8s manifests.
