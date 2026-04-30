using System.Collections.Concurrent;
using Firefly.DependencyInjection;
using Microsoft.Extensions.Logging;
using Victor.Core.Orchestration;

namespace Victor.Slack;

/// <summary>
/// Posts a free-form question to Slack and waits for the user's text reply.
/// SlackListenerService calls <see cref="HandleReply"/> when it detects a
/// message directed at a pending query.
/// </summary>
[RegisterSingleton(Type = typeof(IUserQueryGateway))]
public class SlackUserQueryGateway : IUserQueryGateway
{
    private readonly SlackNotifier _notifier;
    private readonly ILogger<SlackUserQueryGateway> _logger;

    // Key: channelId (or channelId:threadTs) → TCS that the waiting tool call blocks on
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _pending = new();

    public SlackUserQueryGateway(SlackNotifier notifier, ILogger<SlackUserQueryGateway> logger)
    {
        _notifier = notifier;
        _logger = logger;
    }

    public async Task<string?> AskAsync(string jobId, string question, string channelId, string? threadTs, CancellationToken ct = default)
    {
        var key = MakeKey(channelId, threadTs);
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[key] = tcs;

        try
        {
            await _notifier.PostMessageAsync(question, threadTs: threadTs, channelId: channelId, ct: ct);
            _logger.LogInformation("Posted question for job {JobId} in {Key}", jobId, key);

            // Wait for user reply or cancellation (job cancelled / host stopping)
            using var reg = ct.Register(() => tcs.TrySetCanceled(ct));

            // Also apply a 10-minute timeout so we don't block forever
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            await using var timeoutReg = timeout.Token.Register(() => tcs.TrySetResult(null!));

            try
            {
                var reply = await tcs.Task;
                return reply;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }
        finally
        {
            _pending.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// Returns true if there is a pending question waiting for a reply in the
    /// given conversation (channel + optional thread).
    /// </summary>
    public bool HasPendingQuery(string channelId, string? threadTs)
    {
        return _pending.ContainsKey(MakeKey(channelId, threadTs));
    }

    /// <summary>
    /// Called by SlackListenerService when a user message arrives in a
    /// conversation that has a pending query. Routes the reply text to
    /// the waiting AskAsync call.
    /// </summary>
    public bool HandleReply(string channelId, string? threadTs, string text)
    {
        var key = MakeKey(channelId, threadTs);
        if (_pending.TryGetValue(key, out var tcs))
        {
            tcs.TrySetResult(text);
            _logger.LogInformation("User replied to pending query in {Key}", key);
            return true;
        }
        return false;
    }

    private static string MakeKey(string channelId, string? threadTs) =>
        threadTs is not null ? $"{channelId}:{threadTs}" : channelId;
}
