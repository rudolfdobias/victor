using System.Diagnostics;
using System.Text.Json;
using Firefly.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Victor.Core.Abstractions;
using Victor.Core.Models;

namespace Victor.Tools.Shell;

[RegisterSingleton(Type = typeof(ITool))]
public class ShellExecTool : ITool
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "command": {
                    "type": "string",
                    "description": "The shell command to execute"
                }
            },
            "required": ["command"]
        }
        """).RootElement;

    private readonly ShellOptions _options;
    private readonly AuditLogger _audit;
    private readonly ILogger<ShellExecTool> _logger;

    public string Name => "shell_exec";
    public string Description => "Execute a shell command and return stdout, stderr, and exit code.";
    public JsonElement InputSchema => Schema;

    public ShellExecTool(
        IOptions<ShellOptions> options,
        AuditLogger audit,
        ILogger<ShellExecTool> logger)
    {
        _options = options.Value;
        _audit = audit;
        _logger = logger;
    }

    public async Task<ToolResult> ExecuteAsync(JsonElement input, CancellationToken ct = default)
    {
        var command = input.GetProperty("command").GetString()
                     ?? throw new ArgumentException("Missing 'command' property");

        _logger.LogInformation("Executing: {Command}", command);

        var psi = new ProcessStartInfo
        {
            FileName = _options.ShellPath,
            Arguments = $"-c \"{command.Replace("\"", "\\\"")}\"",
            WorkingDirectory = _options.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            return new ToolResult(string.Empty, "Command timed out.", IsError: true);
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        var exitCode = process.ExitCode;

        var output = FormatOutput(stdout, stderr, exitCode);

        await _audit.LogCommandAsync(Guid.Empty, command, exitCode, output, ct);

        return new ToolResult(string.Empty, output, IsError: exitCode != 0);
    }

    private static string FormatOutput(string stdout, string stderr, int exitCode)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(stdout)) parts.Add($"stdout:\n{stdout.TrimEnd()}");
        if (!string.IsNullOrWhiteSpace(stderr)) parts.Add($"stderr:\n{stderr.TrimEnd()}");
        parts.Add($"exit_code: {exitCode}");
        return string.Join("\n\n", parts);
    }
}
