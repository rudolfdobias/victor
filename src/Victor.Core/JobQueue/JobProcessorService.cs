using Firefly.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Victor.Core.Models;
using Victor.Core.Orchestration;

namespace Victor.Core.JobQueue;

[RegisterSingleton(Type = typeof(IHostedService))]
public class JobProcessorService : BackgroundService
{
    private readonly JobQueue _queue;
    private readonly Orchestrator _orchestrator;
    private readonly ILogger<JobProcessorService> _logger;

    public JobProcessorService(
        JobQueue queue,
        Orchestrator orchestrator,
        ILogger<JobProcessorService> logger)
    {
        _queue = queue;
        _orchestrator = orchestrator;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Job processor started, waiting for jobs");

        await foreach (var jobId in _queue.ReadAllAsync(stoppingToken))
        {
            var job = await _queue.GetJobAsync(jobId, stoppingToken);
            if (job is null)
            {
                _logger.LogWarning("Job {JobId} not found, skipping", jobId);
                continue;
            }

            job.Status = JobStatus.Running;
            await _queue.UpdateJobAsync(job, stoppingToken);

            try
            {
                var result = await _orchestrator.RunAsync(job, stoppingToken);
                job.Status = JobStatus.Completed;
                job.Result = result;
                job.CompletedAt = DateTimeOffset.UtcNow;
                await _queue.UpdateJobAsync(job, stoppingToken);
                _logger.LogInformation("Job {JobId} completed", job.Id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                job.Status = JobStatus.Failed;
                job.Error = ex.Message;
                job.CompletedAt = DateTimeOffset.UtcNow;
                await _queue.UpdateJobAsync(job, stoppingToken);
                _logger.LogError(ex, "Job {JobId} failed", job.Id);
            }
        }
    }
}
