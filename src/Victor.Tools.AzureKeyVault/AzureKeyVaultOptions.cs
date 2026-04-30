namespace Victor.Tools.AzureKeyVault;

public class AzureKeyVaultOptions
{
    public const string SectionName = "tools:keyvault";

    public required string VaultUri { get; set; }

    /// <summary>How long retrieved secrets are held in the in-process cache. Default: 5 minutes.</summary>
    public int CacheTtlSeconds { get; set; } = 300;

    // --- Service principal credentials (local development only) ---
    // When all three are set, ClientSecretCredential is used instead of DefaultAzureCredential.
    // In production leave these empty — Workload Identity is used automatically.
    public string? TenantId { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
}
