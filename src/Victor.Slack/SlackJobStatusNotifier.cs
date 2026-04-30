using Firefly.DependencyInjection;
using Microsoft.Extensions.Logging;
using Victor.Core.Orchestration;
using Victor.Models;

namespace Victor.Slack;

[RegisterSingleton(Type = typeof(IJobStatusNotifier))]
public class SlackJobStatusNotifier : IJobStatusNotifier
{
    private readonly SlackNotifier _notifier;
    private readonly ILogger<SlackJobStatusNotifier> _logger;

    public SlackJobStatusNotifier(SlackNotifier notifier, ILogger<SlackJobStatusNotifier> logger)
    {
        _notifier = notifier;
        _logger = logger;
    }

    public Task NotifyPhaseStartedAsync(Job job, PhaseType phase, CancellationToken ct = default)
    {
        // No Slack message on phase start — the ConversationHandler LLM already
        // produced a natural acknowledgment. Posting hardcoded strings here
        // polluted history and made the bot sound robotic.
        _logger.LogInformation("Job {JobId} entering {Phase} phase", job.Id, phase);
        return Task.CompletedTask;
    }

    public async Task NotifyPhaseCompletedAsync(Job job, PhaseType phase, string summary, CancellationToken ct = default)
    {
        if (job.ChannelId is null)
            return;

        // Post the plan summary — it's LLM-generated content, not a template
        if (phase == PhaseType.Planning && !string.IsNullOrWhiteSpace(summary))
        {
            var truncated = summary.Length > 1500
                ? summary[..1500] + "\u2026"
                : summary;
            await PostAsync(job, truncated, ct);
        }
    }

    public async Task NotifyJobCompletedAsync(Job job, CancellationToken ct = default)
    {
        if (job.ChannelId is null)
            return;

        // Post the LLM's final result directly — it's already natural language
        if (!string.IsNullOrWhiteSpace(job.Result))
        {
            var text = job.Result.Length > 1500
                ? job.Result[..1500] + "\u2026"
                : job.Result;
            await PostAsync(job, text, ct);
        }
    }

    public async Task NotifyJobFailedAsync(Job job, string error, CancellationToken ct = default)
    {
        if (job.ChannelId is null)
            return;

        await PostAsync(job, $"Hit a problem and had to stop: {error}", ct);
    }

    private async Task PostAsync(Job job, string text, CancellationToken ct)
    {
        try
        {
            await _notifier.PostMessageAsync(text, threadTs: job.ThreadTs, channelId: job.ChannelId, ct: ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to post thread update for job {JobId}", job.Id);
        }
    }
}
