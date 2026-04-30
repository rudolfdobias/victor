using Victor.Core.Models;

namespace Victor.Core.Abstractions;

/// <summary>
/// Provides conversation history on demand. Implemented by the Slack layer.
/// Used by ConversationHandler when the LLM calls get_history.
/// </summary>
public interface IConversationHistoryProvider
{
    Task<IReadOnlyList<Message>> GetHistoryAsync(string channelId, string? threadTs, CancellationToken ct = default);
}
