using Firefly.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using Victor.Models;

namespace Victor.Tools.Memory;

[RegisterScoped]
public class MemoryStore
{
    private readonly VictorDbContext _db;
    private readonly ILogger<MemoryStore> _logger;

    public MemoryStore(VictorDbContext db, ILogger<MemoryStore> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task StoreAsync(MemoryRecord record, CancellationToken ct = default)
    {
        _db.Memories.Add(record);
        await _db.SaveChangesAsync(ct);
        _logger.LogDebug("Stored memory {Id} for task {TaskId}", record.Id, record.TaskId);
    }

    public async Task<IReadOnlyList<MemoryRecord>> RecallAsync(Vector queryEmbedding, int topK, CancellationToken ct = default)
    {
        var results = await _db.Memories
            .OrderBy(m => m.Embedding.CosineDistance(queryEmbedding))
            .Take(topK)
            .AsNoTracking()
            .ToListAsync(ct);

        _logger.LogDebug("Recalled {Count} memories", results.Count);
        return results;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var rows = await _db.Memories.Where(m => m.Id == id).ExecuteDeleteAsync(ct);
        _logger.LogDebug("Deleted memory {Id}: {Deleted}", id, rows > 0);
        return rows > 0;
    }

    public async Task<MemoryRecord?> UpdateAsync(Guid id, string newSummary, Vector newEmbedding, CancellationToken ct = default)
    {
        var record = await _db.Memories.FindAsync([id], ct);
        if (record is null) return null;

        record.Summary = newSummary;
        record.Embedding = newEmbedding;
        record.Timestamp = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        _logger.LogDebug("Updated memory {Id}", id);
        return record;
    }
}
