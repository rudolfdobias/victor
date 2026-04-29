using Firefly.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Victor.Tools.Shell;

[RegisterSingleton]
public class AuditLogger
{
    private readonly string _logDirectory;
    private readonly ILogger<AuditLogger> _logger;

    public AuditLogger(IOptions<ShellOptions> options, ILogger<AuditLogger> logger)
    {
        _logDirectory = options.Value.AuditLogDirectory;
        _logger = logger;
    }

    public async Task LogCommandAsync(Guid jobId, string command, int exitCode, string output, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_logDirectory);

        var timestamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var logFile = Path.Combine(_logDirectory, $"{jobId}.log");
        var entry = $"[{timestamp}] exit={exitCode} cmd={command}\n{output}\n---\n";

        await File.AppendAllTextAsync(logFile, entry, ct);
        _logger.LogInformation("Audit: job={JobId} exit={ExitCode} cmd={Command}", jobId, exitCode, command);
    }
}
