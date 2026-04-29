using System.Threading.Channels;
using Firefly.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Victor.Core.Data;
using Victor.Core.Models;

namespace Victor.Core.JobQueue;

[RegisterSingleton]
public class JobQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(
        new UnboundedChannelOptions { SingleReader = true });

    private readonly IDbContextFactory<VictorDbContext> _dbFactory;
    private readonly ILogger<JobQueue> _logger;

    public JobQueue(IDbContextFactory<VictorDbContext> dbFactory, ILogger<JobQueue> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task<Job> EnqueueAsync(string description, string requestedBy, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var job = new Job
        {
            Id = Guid.NewGuid(),
            Description = description,
            RequestedBy = requestedBy,
            CreatedAt = DateTimeOffset.UtcNow,
            Status = JobStatus.Queued
        };

        db.Jobs.Add(job);
        await db.SaveChangesAsync(ct);

        _channel.Writer.TryWrite(job.Id);
        _logger.LogInformation("Enqueued job {JobId} from {RequestedBy}", job.Id, requestedBy);
        return job;
    }

    public async Task<Job?> GetJobAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.Jobs.FindAsync([id], ct);
    }

    public async Task<IReadOnlyList<Job>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.Jobs.OrderByDescending(j => j.CreatedAt).ToListAsync(ct);
    }

    public async Task UpdateJobAsync(Job job, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        db.Jobs.Update(job);
        await db.SaveChangesAsync(ct);
    }

    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken ct) =>
        _channel.Reader.ReadAllAsync(ct);
}
