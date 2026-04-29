using Firefly.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SlackNet;
using SlackNet.WebApi;

namespace Victor.Slack;

[RegisterSingleton]
public class SlackNotifier
{
    private readonly ISlackApiClient _slack;
    private readonly SlackOptions _options;
    private readonly ILogger<SlackNotifier> _logger;

    public SlackNotifier(
        ISlackApiClient slack,
        IOptions<SlackOptions> options,
        ILogger<SlackNotifier> logger)
    {
        _slack = slack;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<string> PostMessageAsync(string text, string? threadTs = null, string? channelId = null, CancellationToken ct = default)
    {
        var response = await _slack.Chat.PostMessage(new Message
        {
            Channel = channelId ?? _options.DefaultChannelId,
            Text = text,
            ThreadTs = threadTs
        }, ct);

        _logger.LogDebug("Posted message to {Channel}, ts={Ts}", response.Channel, response.Ts);
        return response.Ts;
    }

    public async Task UpdateMessageAsync(string ts, string text, string? channelId = null, CancellationToken ct = default)
    {
        await _slack.Chat.Update(new MessageUpdate
        {
            ChannelId = channelId ?? _options.DefaultChannelId,
            Ts = ts,
            Text = text
        }, ct);
    }

    public async Task SetPresenceAsync(bool active, CancellationToken ct = default)
    {
        await _slack.Users.SetPresence(active ? RequestPresence.Auto : RequestPresence.Away, ct);
    }
}
