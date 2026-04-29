namespace Victor.Core.Orchestration;

public class PhaseConfig
{
    public const string SectionName = "orchestration:phases";

    public PhaseType Type { get; set; }
    public List<string> AllowedTools { get; set; } = [];
    public TimeSpan ApprovalTimeout { get; set; } = TimeSpan.FromMinutes(5);
    public List<string> SafetyPatterns { get; set; } = [];
}
