# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy central build props first — maximises layer cache hits
COPY Directory.Build.props .
COPY Directory.Packages.props .

# Copy project files for restore
COPY src/Victor.Core/Victor.Core.csproj                         src/Victor.Core/
COPY src/Victor.Providers.OpenAI/Victor.Providers.OpenAI.csproj src/Victor.Providers.OpenAI/
COPY src/Victor.Tools.Shell/Victor.Tools.Shell.csproj           src/Victor.Tools.Shell/
COPY src/Victor.Tools.Memory/Victor.Tools.Memory.csproj         src/Victor.Tools.Memory/
COPY src/Victor.Slack/Victor.Slack.csproj                       src/Victor.Slack/
COPY src/Victor.Host/Victor.Host.csproj                         src/Victor.Host/

RUN dotnet restore src/Victor.Host/Victor.Host.csproj

# Copy sources and publish
COPY src/ src/

RUN dotnet publish src/Victor.Host/Victor.Host.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Workspace for shell audit logs and kubectl operations
RUN mkdir -p /workspace/task_logs

COPY --from=build /app/publish .

# /config is mounted at runtime via a k8s ConfigMap:
#   config.yaml  → main configuration
#   persona.md   → LLM system prompt
VOLUME ["/config", "/workspace"]

EXPOSE 8080

ENTRYPOINT ["./victor"]
