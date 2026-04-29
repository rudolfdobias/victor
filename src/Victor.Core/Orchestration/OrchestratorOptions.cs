namespace Victor.Core.Orchestration;

public class OrchestratorOptions
{
    public const string SectionName = "orchestration";

    public string SystemPrompt { get; set; } = string.Empty;
    public PhaseConfig Research { get; set; } = new() { Type = PhaseType.Research };
    public PhaseConfig Planning { get; set; } = new() { Type = PhaseType.Planning };
    public PhaseConfig Execution { get; set; } = new() { Type = PhaseType.Execution };
}
