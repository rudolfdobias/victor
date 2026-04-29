using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Firefly.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Victor.Core.JobQueue;

namespace Victor.Slack;

[RegisterSingleton(Type = typeof(IHostedService))]
public class SlackListenerService : IHostedService
{
    private readonly SlackOptions _options;
    private readonly JobQueue _jobQueue;
    private readonly SlackNotifier _notifier;
    private readonly SlackApprovalGateway _approvalGateway;
    private readonly ILogger<SlackListenerService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;

    public SlackListenerService(
        IOptions<SlackOptions> options,
        JobQueue jobQueue,
        SlackNotifier notifier,
        SlackApprovalGateway approvalGateway,
        IHttpClientFactory httpClientFactory,
        ILogger<SlackListenerService> logger)
    {
        _options = options.Value;
        _jobQueue = jobQueue;
        _notifier = notifier;
        _approvalGateway = approvalGateway;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await _notifier.SetPresenceAsync(active: true, cancellationToken);
        _listenTask = RunSocketLoopAsync(_cts.Token);
        _logger.LogInformation("Slack listener started");
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        if (_listenTask is not null)
            await _listenTask;
        await _notifier.SetPresenceAsync(active: false, cancellationToken);
        _logger.LogInformation("Slack listener stopped");
    }

    private async Task RunSocketLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var wsUrl = await OpenConnectionAsync(ct);
                await ListenAsync(wsUrl, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Socket Mode connection lost, reconnecting in 5s");
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }

    private async Task<string> OpenConnectionAsync(CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.AppToken);

        var response = await client.PostAsync("https://slack.com/api/apps.connections.open", null, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.GetProperty("ok").GetBoolean())
            throw new InvalidOperationException($"Failed to open Socket Mode connection: {json}");

        return doc.RootElement.GetProperty("url").GetString()!;
    }

    private async Task ListenAsync(string wsUrl, CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(wsUrl), ct);
        _logger.LogInformation("Socket Mode connected");

        var buffer = new byte[16 * 1024];

        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var result = await ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
                break;

            var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
            await ProcessMessageAsync(ws, message, ct);
        }
    }

    private async Task ProcessMessageAsync(ClientWebSocket ws, string raw, CancellationToken ct)
    {
        try
        {
            var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;

            if (!root.TryGetProperty("envelope_id", out var envelopeIdProp))
                return;

            var envelopeId = envelopeIdProp.GetString()!;

            // Acknowledge immediately
            var ack = JsonSerializer.Serialize(new { envelope_id = envelopeId });
            await ws.SendAsync(Encoding.UTF8.GetBytes(ack), WebSocketMessageType.Text, true, ct);

            if (!root.TryGetProperty("type", out var typeProp))
                return;

            var type = typeProp.GetString();

            switch (type)
            {
                case "events_api":
                    await HandleEventAsync(root);
                    break;
                case "interactive":
                    HandleInteraction(root);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing socket message");
        }
    }

    private async Task HandleEventAsync(JsonElement root)
    {
        if (!root.TryGetProperty("payload", out var payload))
            return;

        if (!payload.TryGetProperty("event", out var evt))
            return;

        var eventType = evt.TryGetProperty("type", out var t) ? t.GetString() : null;
        if (eventType is not ("message" and not null) && eventType is not "app_mention")
            return;

        // Ignore bot messages
        if (evt.TryGetProperty("bot_id", out _))
            return;

        var text = evt.TryGetProperty("text", out var textProp) ? textProp.GetString()?.Trim() : null;
        if (string.IsNullOrEmpty(text))
            return;

        var user = evt.TryGetProperty("user", out var userProp) ? userProp.GetString() ?? "unknown" : "unknown";
        var ts = evt.TryGetProperty("ts", out var tsProp) ? tsProp.GetString() : null;
        var channel = evt.TryGetProperty("channel", out var chProp) ? chProp.GetString() : null;

        _logger.LogInformation("Received message from {User}: {Text}", user, text);

        var job = await _jobQueue.EnqueueAsync(text, user);

        _ = _notifier.PostMessageAsync(
            $"Got it. Working on it now. (job `{job.Id}`)",
            threadTs: ts,
            channelId: channel);
    }

    private void HandleInteraction(JsonElement root)
    {
        if (!root.TryGetProperty("payload", out var payload))
            return;

        if (!payload.TryGetProperty("actions", out var actions))
            return;

        foreach (var action in actions.EnumerateArray())
        {
            var actionId = action.TryGetProperty("action_id", out var aProp) ? aProp.GetString() : null;
            if (actionId is null || !actionId.StartsWith("approval-"))
                continue;

            var approved = actionId.EndsWith(":approve");
            _approvalGateway.HandleAction(actionId, approved);
        }
    }
}
