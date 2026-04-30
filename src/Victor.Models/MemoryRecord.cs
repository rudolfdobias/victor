using Pgvector;

namespace Victor.Models;

public class MemoryRecord
{
    public Guid Id { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public Guid TaskId { get; set; }
    public string Category { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public Vector Embedding { get; set; } = null!;
}
