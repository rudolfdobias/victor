using System.Collections.Concurrent;
using Firefly.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SlackNet;
using SlackNet.Events;
using Victor.Core.Abstractions;
using Victor.Core.Models;

namespace Victor.Slack;

/// <summary>
/// Fetches Slack conversation history and maps it to <see cref="Message"/> for LLM context.
/// Resolves bot identity at first use and caches user display names.
/// </summary>
[RegisterSingleton(Type = typeof(IConversationHistoryProvider))]
[RegisterSingleton]
public class SlackHistoryService : IConversationHistoryProvider
{
    private readonly ISlackApiClient _slack;
    private readonly SlackOptions _options;
    private readonly ILogger<SlackHistoryService> _logger;

    private readonly ConcurrentDictionary<string, string> _userNameCache = new();
    private string? _botUserId;

    public SlackHistoryService(
        ISlackApiClient slack,
        IOptions<SlackOptions> options,
        ILogger<SlackHistoryService> logger)
    {
        _slack = slack;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Resolves the bot's own user ID via auth.test. Called once, result is cached.
    /// </summary>
    public async Task<string> GetBotUserIdAsync(CancellationToken ct = default)
    {
        if (_botUserId is not null)
            return _botUserId;

        var authResponse = await _slack.Auth.Test(ct);
        _botUserId = authResponse.UserId;
        _logger.LogInformation("Bot user ID resolved: {BotUserId}", _botUserId);
        return _botUserId;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<Message>> GetHistoryAsync(string channelId, string? threadTs, CancellationToken ct = default) =>
        threadTs is not null
            ? GetThreadHistoryAsync(channelId, threadTs, ct)
            : GetChannelHistoryAsync(channelId, ct);

    /// <summary>
    /// Fetches conversation history (channel or DM) and maps to Messages.
    /// </summary>
    public async Task<IReadOnlyList<Message>> GetChannelHistoryAsync(
        string channelId,
        CancellationToken ct = default)
    {
        var botUserId = await GetBotUserIdAsync(ct);

        var response = await _slack.Conversations.History(
            channelId,
            limit: _options.HistoryMessageCount,
            cancellationToken: ct);

        // Slack returns newest first — reverse for chronological order
        var slackMessages = response.Messages;
        slackMessages.Reverse();

        return await MapMessagesAsync(slackMessages, botUserId, ct);
    }

    /// <summary>
    /// Fetches thread replies and maps to Messages.
    /// </summary>
    public async Task<IReadOnlyList<Message>> GetThreadHistoryAsync(
        string channelId,
        string threadTs,
        CancellationToken ct = default)
    {
        var botUserId = await GetBotUserIdAsync(ct);

        var response = await _slack.Conversations.Replies(
            channelId,
            threadTs,
            limit: _options.HistoryMessageCount,
            cancellationToken: ct);

        // Replies come in chronological order already
        return await MapMessagesAsync(response.Messages, botUserId, ct);
    }

    private async Task<IReadOnlyList<Message>> MapMessagesAsync(
        IList<MessageEvent> slackMessages,
        string botUserId,
        CancellationToken ct)
    {
        var messages = new List<Message>(slackMessages.Count);

        foreach (var msg in slackMessages)
        {
            if (string.IsNullOrEmpty(msg.Text))
                continue;

            // Skip non-message subtypes (channel_join, bot_message from other bots, etc.)
            if (!string.IsNullOrEmpty(msg.Subtype) && msg.Subtype != "bot_message")
                continue;

            // Skip automated job status notifications (old and new format) — they're
            // noise that causes the LLM to mimic templates instead of responding naturally.
            if ((msg.User == botUserId || !string.IsNullOrEmpty(msg.BotId))
                && IsAutomatedStatusMessage(msg.Text))
                continue;

            var timestamp = FormatSlackTimestamp(msg.Ts);

            if (msg.User == botUserId || !string.IsNullOrEmpty(msg.BotId))
            {
                messages.Add(new Message(Role.Assistant, $"[{timestamp}] {msg.Text}"));
            }
            else
            {
                var displayName = await ResolveUserNameAsync(msg.User, ct);
                messages.Add(new Message(Role.User, $"[{timestamp} from @{displayName}] {msg.Text}"));
            }
        }

        return messages;
    }

    /// <summary>
    /// Matches bot messages that are automated status notifications rather than
    /// real conversational replies. Covers both the old hardcoded format and
    /// any future prefixed format.
    /// </summary>
    private static bool IsAutomatedStatusMessage(string text)
    {
        // Old responses that embedded timestamps in the message text (from earlier deployments).
        // Raw Slack text starting with "[2026-..." means it's an old-format bot response.
        if (text.Length > 12 && text[0] == '[' && char.IsDigit(text[1]) && text[5] == '-')
            return true;

        // Old hardcoded phase-start messages from SlackJobStatusNotifier
        if (text.StartsWith("Starting research", StringComparison.OrdinalIgnoreCase)) return true;
        if (text.StartsWith("Starting execution", StringComparison.OrdinalIgnoreCase)) return true;
        if (text.StartsWith("Research done.", StringComparison.OrdinalIgnoreCase)) return true;
        if (text.StartsWith("Plan ready.", StringComparison.OrdinalIgnoreCase)) return true;
        if (text.StartsWith("Entering ", StringComparison.OrdinalIgnoreCase) && text.Contains(" phase")) return true;
        if (text.StartsWith("Job failed:", StringComparison.OrdinalIgnoreCase)) return true;
        if (text.StartsWith("[status] ", StringComparison.Ordinal)) return true;
        // Bare "Done." without any context
        if (text is "Done.") return true;
        return false;
    }

    private static string FormatSlackTimestamp(string? ts)
    {
        if (ts is null || !double.TryParse(ts, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var unixSeconds))
            return "unknown time";

        var dt = DateTimeOffset.FromUnixTimeMilliseconds((long)(unixSeconds * 1000));
        return dt.ToString("yyyy-MM-dd HH:mm");
    }

    private async Task<string> ResolveUserNameAsync(string userId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(userId))
            return "unknown";

        if (_userNameCache.TryGetValue(userId, out var cached))
            return cached;

        try
        {
            var userInfo = await _slack.Users.Info(userId, cancellationToken: ct);
            var name = userInfo.Profile.DisplayName;
            if (string.IsNullOrEmpty(name))
                name = userInfo.Profile.RealName;
            if (string.IsNullOrEmpty(name))
                name = userInfo.Name;

            _userNameCache[userId] = name;
            return name;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve user name for {UserId}", userId);
            _userNameCache[userId] = userId;
            return userId;
        }
    }
}
