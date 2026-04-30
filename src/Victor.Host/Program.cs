using Azure.Extensions.AspNetCore.Configuration.Secrets;
using Azure.Identity;
using Firefly.DependencyInjection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using NetEscapades.Configuration.Yaml;
using Serilog;
using Victor.Core.JobQueue;
using Victor.Models;
using Victor.Core.Conversation;
using Victor.Core.Orchestration;
using Victor.Providers.OpenAI;
using Victor.Slack;
using Victor.Tools.AzureKeyVault;
using Victor.Tools.Memory;
using Victor.Tools.Shell;

var builder = WebApplication.CreateBuilder(args);

// YAML configuration — replaces appsettings.json
// In Kubernetes the ConfigMap mounts to /config/config.yaml.
// For local development, place config.yaml next to the binary.
builder.Configuration.Sources.Clear();
var configPath = File.Exists("/config/config.yaml")
    ? "/config/config.yaml"
    : Path.Combine(AppContext.BaseDirectory, "ConfigFiles/config.yaml");
builder.Configuration
    .AddYamlFile(configPath, optional: false, reloadOnChange: true)
    .AddEnvironmentVariables()
    .AddCommandLine(args);

// Azure Key Vault (Workload Identity) — optional, skip when endpoint is absent
var keyVaultEndpoint = builder.Configuration["keyVault:endpoint"];
if (!string.IsNullOrWhiteSpace(keyVaultEndpoint))
{
    builder.Configuration.AddAzureKeyVault(
        new Uri(keyVaultEndpoint),
        new DefaultAzureCredential());
}

// Structured logging via Serilog
builder.Host.UseSerilog((ctx, _, config) => config
    .ReadFrom.Configuration(ctx.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console(
        outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}"));

var services = builder.Services;

// Firefly attribute-based DI — scan all project assemblies
services.AddFireflyServiceRegistration(b =>
{
    b.UseAssembly("Victor.Models");
    b.UseAssembly("Victor.Core");
    b.UseAssembly("Victor.Providers.OpenAI");
    b.UseAssembly("Victor.Slack");
    b.UseAssembly("Victor.Tools.AzureKeyVault");
    b.UseAssembly("Victor.Tools.Memory");
    b.UseAssembly("Victor.Tools.Shell");
    b.RegisterAllImplementations();
});

// Slack — manual wiring (SlackNet is a third-party library)
services.AddVictorSlack(builder.Configuration);

// EF Core — shared VictorDbContext with pgvector
var connectionString = builder.Configuration["database:connectionString"]
    ?? throw new InvalidOperationException("database:connectionString is required");

services.AddDbContextFactory<VictorDbContext>(opt =>
    opt.UseNpgsql(connectionString, npgsql => npgsql.UseVector()));

// IOptions<T> bindings
services.Configure<OrchestratorOptions>(builder.Configuration.GetSection(OrchestratorOptions.SectionName));
services.PostConfigure<OrchestratorOptions>(options =>
{
    // Load system prompt from file if systemPrompt is not set inline.
    // The file path is provided via orchestration:systemPromptFile in config.yaml
    // and the file itself is provided by infrastructure (e.g. a k8s ConfigMap).
    if (string.IsNullOrWhiteSpace(options.SystemPrompt))
    {
        var file = builder.Configuration["orchestration:systemPromptFile"];
        if (!string.IsNullOrWhiteSpace(file))
            options.SystemPrompt = File.ReadAllText(file);
    }
});

services.Configure<ConversationHandlerOptions>(builder.Configuration.GetSection(ConversationHandlerOptions.SectionName));
services.Configure<OpenAIOptions>(builder.Configuration.GetSection(OpenAIOptions.SectionName));
services.Configure<ShellOptions>(builder.Configuration.GetSection(ShellOptions.SectionName));
services.Configure<MemoryOptions>(builder.Configuration.GetSection(MemoryOptions.SectionName));
services.Configure<AzureKeyVaultOptions>(builder.Configuration.GetSection(AzureKeyVaultOptions.SectionName));

// Health checks
services.AddHealthChecks()
    .AddDbContextCheck<VictorDbContext>("database", tags: ["ready"]);

var app = builder.Build();

app.MapHealthChecks("/healthz/live", new HealthCheckOptions
{
    Predicate = _ => false  // liveness: always 200 if the process is up
});

app.MapHealthChecks("/healthz/ready", new HealthCheckOptions
{
    Predicate = r => r.Tags.Contains("ready")
});

// // Test endpoint — enqueue a job without Slack. Not for production use.
// app.MapPost("/jobs", async (EnqueueRequest req, JobQueue queue, CancellationToken ct) =>
// {
//     if (string.IsNullOrWhiteSpace(req.Description))
//         return Results.BadRequest("description is required");
//
//     var job = await queue.EnqueueAsync(req.Description, req.RequestedBy ?? "api", ct);
//     return Results.Ok(new { job.Id, job.Status, job.CreatedAt });
// });
//
// app.MapGet("/jobs/{id:guid}", async (Guid id, JobQueue queue, CancellationToken ct) =>
// {
//     var job = await queue.GetJobAsync(id, ct);
//     return job is null
//         ? Results.NotFound()
//         : Results.Ok(new { job.Id, job.Status, job.Result, job.Error, job.CreatedAt, job.CompletedAt });
// });

app.Run();

record EnqueueRequest(string Description, string? RequestedBy);
