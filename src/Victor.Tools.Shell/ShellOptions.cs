namespace Victor.Tools.Shell;

public class ShellOptions
{
    public const string SectionName = "tools:shell";

    public string ShellPath { get; set; } = "/bin/bash";
    public string WorkingDirectory { get; set; } = "/workspace";
    public string AuditLogDirectory { get; set; } = "/workspace/task_logs";
    public int TimeoutSeconds { get; set; } = 120;
    public IReadOnlyList<string> SafetyPatterns { get; set; } = Array.Empty<string>();
}
