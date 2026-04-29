using Firefly.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using Victor.Core.Data;
using Victor.Core.Models;

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
}
