namespace Victor.Tools.Memory;

public class MemoryOptions
{
    public const string SectionName = "tools:memory";

    public int RecallTopK { get; set; } = 5;
}
