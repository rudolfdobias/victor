using Victor.Models;

namespace Victor.Core.Orchestration;

/// <summary>
/// Notifies external channels (e.g. Slack) about job progress.
/// Called by Orchestrator at phase transitions and job completion.
/// </summary>
public interface IJobStatusNotifier
{
    Task NotifyPhaseStartedAsync(Job job, PhaseType phase, CancellationToken ct = default);
    Task NotifyPhaseCompletedAsync(Job job, PhaseType phase, string summary, CancellationToken ct = default);
    Task NotifyJobCompletedAsync(Job job, CancellationToken ct = default);
    Task NotifyJobFailedAsync(Job job, string error, CancellationToken ct = default);
}
