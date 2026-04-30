using System.Collections.Concurrent;
using System.Threading.Channels;
using Firefly.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Victor.Core.Models;
using Victor.Models;

namespace Victor.Core.JobQueue;

[RegisterSingleton]
public class JobQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(
        new UnboundedChannelOptions { SingleReader = true });

    private readonly ConcurrentDictionary<Guid, IReadOnlyList<Message>> _conversationContext = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _jobCts = new();
    private readonly IDbContextFactory<VictorDbContext> _dbFactory;
    private readonly ILogger<JobQueue> _logger;

    public JobQueue(IDbContextFactory<VictorDbContext> dbFactory, ILogger<JobQueue> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task<Job> EnqueueAsync(
        string description,
        string requestedBy,
        IReadOnlyList<Message>? conversationHistory = null,
        string? channelId = null,
        string? threadTs = null,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var job = new Job
        {
            Id = Guid.NewGuid(),
            Description = description,
            RequestedBy = requestedBy,
            ChannelId = channelId,
            ThreadTs = threadTs,
            CreatedAt = DateTimeOffset.UtcNow,
            Status = JobStatus.Queued
        };

        db.Jobs.Add(job);
        await db.SaveChangesAsync(ct);

        if (conversationHistory is { Count: > 0 })
            _conversationContext[job.Id] = conversationHistory;

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

    public IReadOnlyList<Message>? TakeConversationContext(Guid jobId)
    {
        _conversationContext.TryRemove(jobId, out var context);
        return context;
    }

    public CancellationTokenSource RegisterJobCts(Guid jobId, CancellationToken linked)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(linked);
        _jobCts[jobId] = cts;
        return cts;
    }

    public void UnregisterJobCts(Guid jobId)
    {
        if (_jobCts.TryRemove(jobId, out var cts))
            cts.Dispose();
    }

    public async Task<bool> CancelJobAsync(Guid jobId, CancellationToken ct = default)
    {
        // If it's currently running, signal cancellation
        if (_jobCts.TryGetValue(jobId, out var cts))
        {
            await cts.CancelAsync();
            _logger.LogInformation("Cancelled running job {JobId}", jobId);
            return true;
        }

        // If it's queued but not yet picked up, mark it directly
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var job = await db.Jobs.FindAsync([jobId], ct);
        if (job is null) return false;

        if (job.Status == JobStatus.Queued)
        {
            job.Status = JobStatus.Cancelled;
            job.CompletedAt = DateTimeOffset.UtcNow;
            job.LastStatusMessage = "Cancelled before execution";
            await db.SaveChangesAsync(ct);
            _logger.LogInformation("Cancelled queued job {JobId}", jobId);
            return true;
        }

        return false;
    }

    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken ct) =>
        _channel.Reader.ReadAllAsync(ct);
}
