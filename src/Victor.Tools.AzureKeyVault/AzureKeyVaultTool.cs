using System.Collections.Concurrent;
using System.Text.Json;
using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Firefly.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Victor.Core.Abstractions;
using Victor.Core.Models;

namespace Victor.Tools.AzureKeyVault;

[RegisterSingleton(Type = typeof(ITool))]
public class AzureKeyVaultTool : ITool
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "action": {
                    "type": "string",
                    "enum": ["get", "set", "list", "delete"],
                    "description": "Operation to perform on Azure Key Vault"
                },
                "name": {
                    "type": "string",
                    "description": "Secret name (required for get, set, delete)"
                },
                "value": {
                    "type": "string",
                    "description": "Secret value (required for set)"
                }
            },
            "required": ["action"]
        }
        """).RootElement;

    private readonly SecretClient? _client;
    private readonly TimeSpan _cacheTtl;
    private readonly ILogger<AzureKeyVaultTool> _logger;

    // name -> (value, expiry)
    private readonly ConcurrentDictionary<string, (string Value, DateTimeOffset ExpiresAt)> _cache = new();

    public string Name => "keyvault";
    public string Description => "Read and write secrets in Azure Key Vault. Use this instead of shell commands for all secret operations.";
    public JsonElement InputSchema => Schema;

    public AzureKeyVaultTool(IOptions<AzureKeyVaultOptions> options, ILogger<AzureKeyVaultTool> logger)
    {
        var opts = options.Value;
        _cacheTtl = TimeSpan.FromSeconds(opts.CacheTtlSeconds);
        _logger = logger;

        if (!Uri.TryCreate(opts.VaultUri, UriKind.Absolute, out var vaultUri))
        {
            _logger.LogWarning("tools:keyvault:vaultUri is not configured or invalid — KeyVault tool is disabled");
            return;
        }

        TokenCredential credential =
            !string.IsNullOrWhiteSpace(opts.TenantId) &&
            !string.IsNullOrWhiteSpace(opts.ClientId) &&
            !string.IsNullOrWhiteSpace(opts.ClientSecret)
                ? new ClientSecretCredential(opts.TenantId, opts.ClientId, opts.ClientSecret)
                : new DefaultAzureCredential();

        _client = new SecretClient(vaultUri, credential);
    }

    public Task<ToolResult> ExecuteAsync(JsonElement input, CancellationToken ct = default)
    {
        if (_client is null)
            return Task.FromResult(new ToolResult(string.Empty, "KeyVault tool is not configured (tools:keyvault:vaultUri is missing).", IsError: true));

        var action = input.GetProperty("action").GetString();

        return action switch
        {
            "get"    => GetAsync(input, ct),
            "set"    => SetAsync(input, ct),
            "list"   => ListAsync(ct),
            "delete" => DeleteAsync(input, ct),
            _        => Task.FromResult(new ToolResult(string.Empty, $"Unknown action: {action}", IsError: true))
        };
    }

    private async Task<ToolResult> GetAsync(JsonElement input, CancellationToken ct)
    {
        var name = RequireName(input, out var err);
        if (name is null) return err!;

        if (_cache.TryGetValue(name, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
        {
            _logger.LogDebug("Cache hit for secret '{Name}'", name);
            return new ToolResult(string.Empty, cached.Value);
        }

        try
        {
            var response = await _client.GetSecretAsync(name, cancellationToken: ct);
            var value = response.Value.Value;

            _cache[name] = (value, DateTimeOffset.UtcNow.Add(_cacheTtl));
            _logger.LogInformation("Retrieved secret '{Name}' from Key Vault", name);

            return new ToolResult(string.Empty, value);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return new ToolResult(string.Empty, $"Secret '{name}' not found in Key Vault.", IsError: true);
        }
        catch (RequestFailedException ex)
        {
            _logger.LogError(ex, "Failed to get secret '{Name}'", name);
            return new ToolResult(string.Empty, $"Key Vault error: {ex.Message}", IsError: true);
        }
    }

    private async Task<ToolResult> SetAsync(JsonElement input, CancellationToken ct)
    {
        var name = RequireName(input, out var err);
        if (name is null) return err!;

        if (!input.TryGetProperty("value", out var valueProp) || valueProp.GetString() is not string value)
            return new ToolResult(string.Empty, "Missing required property: value", IsError: true);

        try
        {
            await _client.SetSecretAsync(name, value, ct);

            // Write-through: update cache immediately
            _cache[name] = (value, DateTimeOffset.UtcNow.Add(_cacheTtl));
            _logger.LogInformation("Set secret '{Name}' in Key Vault", name);

            return new ToolResult(string.Empty, $"Secret '{name}' saved.");
        }
        catch (RequestFailedException ex)
        {
            _logger.LogError(ex, "Failed to set secret '{Name}'", name);
            return new ToolResult(string.Empty, $"Key Vault error: {ex.Message}", IsError: true);
        }
    }

    private async Task<ToolResult> ListAsync(CancellationToken ct)
    {
        try
        {
            var names = new List<string>();
            await foreach (var prop in _client.GetPropertiesOfSecretsAsync(ct))
            {
                if (prop.Enabled == true)
                    names.Add(prop.Name);
            }

            _logger.LogInformation("Listed {Count} secrets from Key Vault", names.Count);
            return new ToolResult(string.Empty, names.Count == 0
                ? "No secrets found."
                : string.Join("\n", names));
        }
        catch (RequestFailedException ex)
        {
            _logger.LogError(ex, "Failed to list secrets");
            return new ToolResult(string.Empty, $"Key Vault error: {ex.Message}", IsError: true);
        }
    }

    private async Task<ToolResult> DeleteAsync(JsonElement input, CancellationToken ct)
    {
        var name = RequireName(input, out var err);
        if (name is null) return err!;

        try
        {
            await _client.StartDeleteSecretAsync(name, ct);

            _cache.TryRemove(name, out _);
            _logger.LogInformation("Deleted secret '{Name}' from Key Vault", name);

            return new ToolResult(string.Empty, $"Secret '{name}' deleted (soft-delete initiated).");
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return new ToolResult(string.Empty, $"Secret '{name}' not found.", IsError: true);
        }
        catch (RequestFailedException ex)
        {
            _logger.LogError(ex, "Failed to delete secret '{Name}'", name);
            return new ToolResult(string.Empty, $"Key Vault error: {ex.Message}", IsError: true);
        }
    }

    private static string? RequireName(JsonElement input, out ToolResult? error)
    {
        if (input.TryGetProperty("name", out var prop) && prop.GetString() is string name)
        {
            error = null;
            return name;
        }
        error = new ToolResult(string.Empty, "Missing required property: name", IsError: true);
        return null;
    }
}
