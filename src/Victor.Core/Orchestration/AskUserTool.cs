using System.Text.Json;
using Firefly.DependencyInjection;
using Microsoft.Extensions.Logging;
using Victor.Core.Abstractions;
using Victor.Core.Models;
using Victor.Models;

namespace Victor.Core.Orchestration;

/// <summary>
/// Tool available to the Orchestrator during job phases. Posts a question to
/// Slack and blocks until the user replies (or the job is cancelled).
/// </summary>
[RegisterSingleton(Type = typeof(ITool))]
public class AskUserTool : ITool
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "question": {
                    "type": "string",
                    "description": "The question to ask the user. Be specific about what you need to know and why."
                }
            },
            "required": ["question"]
        }
        """).RootElement;

    private readonly IUserQueryGateway _gateway;
    private readonly ILogger<AskUserTool> _logger;

    // The current job context is set by the Orchestrator before each phase run.
    // This is a singleton, so we use AsyncLocal to be safe.
    private static readonly AsyncLocal<Job?> _currentJob = new();

    public static void SetCurrentJob(Job? job) => _currentJob.Value = job;

    public AskUserTool(IUserQueryGateway gateway, ILogger<AskUserTool> logger)
    {
        _gateway = gateway;
        _logger = logger;
    }

    public string Name => "ask_user";

    public string Description =>
        "Ask the user a question and wait for their reply. Use this when you need clarification, " +
        "encounter ambiguity, or need the user to make a decision before you can proceed. " +
        "The user will see your question in Slack and can reply in their own time.";

    public JsonElement InputSchema => Schema;

    public async Task<ToolResult> ExecuteAsync(JsonElement input, CancellationToken ct = default)
    {
        var question = input.TryGetProperty("question", out var q) ? q.GetString() : null;
        if (string.IsNullOrWhiteSpace(question))
            return new ToolResult("ask_user", "Missing or empty 'question'.", IsError: true);

        var job = _currentJob.Value;
        if (job?.ChannelId is null)
            return new ToolResult("ask_user", "No channel context — cannot reach the user.", IsError: true);

        _logger.LogInformation("Job {JobId} asking user: {Question}", job.Id, question);

        var reply = await _gateway.AskAsync(
            job.Id.ToString(), question, job.ChannelId, job.ThreadTs, ct);

        if (reply is null)
            return new ToolResult("ask_user", "User did not reply (timed out). Proceed with your best judgment or stop.", IsError: false);

        _logger.LogInformation("Job {JobId} got user reply: {Reply}", job.Id,
            reply.Length > 200 ? reply[..200] + "..." : reply);

        return new ToolResult("ask_user", reply);
    }
}
