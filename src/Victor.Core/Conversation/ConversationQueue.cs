using System.Collections.Concurrent;
using Firefly.DependencyInjection;

namespace Victor.Core.Conversation;

/// <summary>
/// Per-conversation serialization layer. Ensures that two messages from the same
/// conversation are never processed concurrently. Different conversations run in parallel.
/// Key = conversation_id (DM channel, channel+thread_ts, or channel).
/// </summary>
[RegisterSingleton]
public class ConversationQueue
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public async Task<T> ExecuteAsync<T>(string conversationId, Func<Task<T>> action, CancellationToken ct = default)
    {
        var semaphore = _locks.GetOrAdd(conversationId, _ => new SemaphoreSlim(1, 1));

        await semaphore.WaitAsync(ct);
        try
        {
            return await action();
        }
        finally
        {
            semaphore.Release();
        }
    }
}
