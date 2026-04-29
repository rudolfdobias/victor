namespace Victor.Tools.Memory;

public class MemoryOptions
{
    public const string SectionName = "tools:memory";

    public required string ConnectionString { get; set; }
    public int RecallTopK { get; set; } = 5;
}
