using Firefly.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Victor.Models;
using Victor.Core.Orchestration;

namespace Victor.Core.JobQueue;

[RegisterSingleton(Type = typeof(IHostedService))]
public class JobProcessorService : BackgroundService
{
    private readonly JobQueue _queue;
    private readonly Orchestrator _orchestrator;
    private readonly IJobStatusNotifier _statusNotifier;
    private readonly ILogger<JobProcessorService> _logger;

    public JobProcessorService(
        JobQueue queue,
        Orchestrator orchestrator,
        IJobStatusNotifier statusNotifier,
        ILogger<JobProcessorService> logger)
    {
        _queue = queue;
        _orchestrator = orchestrator;
        _statusNotifier = statusNotifier;
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

            // Skip jobs that were cancelled while queued
            if (job.Status == JobStatus.Cancelled)
            {
                _logger.LogInformation("Job {JobId} was cancelled while queued, skipping", jobId);
                continue;
            }

            var conversationHistory = _queue.TakeConversationContext(jobId);
            job.Status = JobStatus.Running;
            await _queue.UpdateJobAsync(job, stoppingToken);

            using var jobCts = _queue.RegisterJobCts(jobId, stoppingToken);
            try
            {
                var result = await _orchestrator.RunAsync(job, conversationHistory, jobCts.Token);
                job.Status = JobStatus.Completed;
                job.Result = result;
                job.CompletedAt = DateTimeOffset.UtcNow;
                await _queue.UpdateJobAsync(job, stoppingToken);
                await _statusNotifier.NotifyJobCompletedAsync(job, stoppingToken);
                _logger.LogInformation("Job {JobId} completed", job.Id);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                job.Status = JobStatus.Cancelled;
                job.LastStatusMessage = "Cancelled by user";
                job.CompletedAt = DateTimeOffset.UtcNow;
                await _queue.UpdateJobAsync(job, stoppingToken);
                await _statusNotifier.NotifyJobFailedAsync(job, "Job was cancelled.", stoppingToken);
                _logger.LogInformation("Job {JobId} cancelled", job.Id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                job.Status = JobStatus.Failed;
                job.Error = ex.Message;
                job.CompletedAt = DateTimeOffset.UtcNow;
                await _queue.UpdateJobAsync(job, stoppingToken);
                await _statusNotifier.NotifyJobFailedAsync(job, ex.Message, stoppingToken);
                _logger.LogError(ex, "Job {JobId} failed", job.Id);
            }
            finally
            {
                _queue.UnregisterJobCts(jobId);
            }
        }
    }
}
