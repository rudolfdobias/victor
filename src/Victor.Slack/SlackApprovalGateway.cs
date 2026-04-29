using System.Collections.Concurrent;
using Firefly.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SlackNet;
using SlackNet.Blocks;
using SlackNet.WebApi;
using Victor.Core.Orchestration;

namespace Victor.Slack;

[RegisterSingleton(Type = typeof(IApprovalGateway))]
public class SlackApprovalGateway : IApprovalGateway
{
    private readonly ISlackApiClient _slack;
    private readonly SlackOptions _options;
    private readonly ILogger<SlackApprovalGateway> _logger;

    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pending = new();

    public SlackApprovalGateway(
        ISlackApiClient slack,
        IOptions<SlackOptions> options,
        ILogger<SlackApprovalGateway> logger)
    {
        _slack = slack;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<bool> RequestApprovalAsync(string jobId, string toolName, string command, CancellationToken ct = default)
    {
        var approvalId = $"approval-{jobId}-{Guid.NewGuid():N}";
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[approvalId] = tcs;

        try
        {
            await _slack.Chat.PostMessage(new Message
            {
                Channel = _options.DefaultChannelId,
                Text = $"Approval required for job `{jobId}`",
                Blocks = new List<Block>
                {
                    new SectionBlock
                    {
                        Text = new Markdown($"*Approval Required*\nTool: `{toolName}`\n```\n{command}\n```")
                    },
                    new ActionsBlock
                    {
                        Elements = new List<IActionElement>
                        {
                            new Button
                            {
                                Text = new PlainText("Approve"),
                                ActionId = $"{approvalId}:approve",
                                Style = ButtonStyle.Primary
                            },
                            new Button
                            {
                                Text = new PlainText("Reject"),
                                ActionId = $"{approvalId}:reject",
                                Style = ButtonStyle.Danger
                            }
                        }
                    }
                }
            }, ct);

            _logger.LogInformation("Approval requested: {ApprovalId} for {Tool}", approvalId, toolName);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(5));
            await using (cts.Token.Register(() => tcs.TrySetResult(false)))
            {
                return await tcs.Task;
            }
        }
        finally
        {
            _pending.TryRemove(approvalId, out _);
        }
    }

    public void HandleAction(string actionId, bool approved)
    {
        var approvalId = actionId.Split(':')[0];
        if (_pending.TryGetValue(approvalId, out var tcs))
        {
            tcs.TrySetResult(approved);
            _logger.LogInformation("Approval {ApprovalId}: {Result}", approvalId, approved ? "approved" : "rejected");
        }
    }
}
