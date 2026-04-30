using System.Text.Json;
using Firefly.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Victor.Core.Abstractions;
using Victor.Core.Models;
using Victor.Core.Orchestration;
using Victor.Models;

namespace Victor.Core.Conversation;

/// <summary>
/// Handles synchronous conversational messages. Runs an LLM turn with read-only tools
/// and a <c>start_job</c> pseudo-tool. Never mutates infrastructure directly.
/// </summary>
[RegisterSingleton]
public class ConversationHandler
{
    private static readonly JsonElement StartJobSchema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "description": {
                    "type": "string",
                    "description": "A detailed description of the job to perform. Include all relevant context: what to do, where (cluster, namespace, environment), and any constraints."
                }
            },
            "required": ["description"]
        }
        """).RootElement;

    private readonly ILLMProvider _llm;
    private readonly IReadOnlyDictionary<string, ITool> _tools;
    private readonly JobQueue.JobQueue _jobQueue;
    private readonly IDbContextFactory<VictorDbContext> _dbFactory;
    private readonly ConversationHandlerOptions _options;
    private readonly OrchestratorOptions _orchestratorOptions;
    private readonly ILogger<ConversationHandler> _logger;
    private readonly StartJobToolProxy _startJobProxy;

    public ConversationHandler(
        ILLMProvider llm,
        IEnumerable<ITool> tools,
        JobQueue.JobQueue jobQueue,
        IDbContextFactory<VictorDbContext> dbFactory,
        IOptions<ConversationHandlerOptions> options,
        IOptions<OrchestratorOptions> orchestratorOptions,
        ILogger<ConversationHandler> logger)
    {
        _llm = llm;
        _tools = tools.ToDictionary(t => t.Name, t => t);
        _jobQueue = jobQueue;
        _dbFactory = dbFactory;
        _options = options.Value;
        _orchestratorOptions = orchestratorOptions.Value;
        _logger = logger;
        _startJobProxy = new StartJobToolProxy();
    }

    /// <summary>
    /// Process a conversational message and return Victor's text reply.
    /// Messages may be a single current message (standalone) or full history (follow-up,
    /// determined by the triage classifier upstream).
    /// </summary>
    public async Task<string> HandleAsync(
        IReadOnlyList<Message> conversationHistory,
        string userId,
        string? channelId = null,
        string? threadTs = null,
        CancellationToken ct = default)
    {
        var allowedTools = BuildToolList();
        _logger.LogDebug("ConversationHandler tools: [{Tools}]",
            string.Join(", ", allowedTools.Select(t => t.Name)));
        _logger.LogDebug("ConversationHandler received {Count} messages for user {User}",
            conversationHistory.Count, userId);

        var systemPrompt = await BuildSystemPromptAsync(userId, ct);
        var messages = new List<Message>(conversationHistory);

        for (var iteration = 0; iteration < _options.MaxToolIterations; iteration++)
        {
            ct.ThrowIfCancellationRequested();

            _logger.LogDebug("LLM iteration {Iteration}/{Max}, {MsgCount} messages",
                iteration + 1, _options.MaxToolIterations, messages.Count);

            var request = new LLMRequest(systemPrompt, messages, allowedTools);
            var response = await _llm.CompleteAsync(request, ct);

            _logger.LogDebug("LLM response: stop={StopReason}, toolCalls={ToolCount}, content={Content}",
                response.StopReason,
                response.ToolUses?.Count ?? 0,
                response.Content.Length > 300 ? response.Content[..300] + "…" : response.Content);

            if (response.StopReason == StopReason.EndTurn || response.ToolUses is null or { Count: 0 })
                return response.Content;

            messages.Add(new Message(Role.Assistant, response.Content));

            foreach (var toolUse in response.ToolUses)
            {
                _logger.LogDebug("Tool call: {Tool} input={Input}", toolUse.ToolName,
                    toolUse.Input.ToString()?.Length > 200
                        ? toolUse.Input.ToString()![..200] + "…"
                        : toolUse.Input.ToString());

                var result = await ExecuteToolAsync(toolUse, userId, channelId, threadTs, ct);

                _logger.LogDebug("Tool result: {Tool} error={IsError} output={Output}", toolUse.ToolName,
                    result.IsError,
                    result.Output.Length > 300 ? result.Output[..300] + "…" : result.Output);

                messages.Add(new Message(Role.User, $"[Tool: {toolUse.ToolName}] {result.Output}"));
            }
        }

        _logger.LogWarning("ConversationHandler hit max iterations ({Max}) for user {User}",
            _options.MaxToolIterations, userId);

        // One final call without tools to force a text reply
        var finalRequest = new LLMRequest(systemPrompt, messages);
        var finalResponse = await _llm.CompleteAsync(finalRequest, ct);
        return finalResponse.Content;
    }

    private List<ITool> BuildToolList()
    {
        var tools = new List<ITool>();

        foreach (var toolName in _options.AllowedTools)
        {
            if (toolName == "start_job")
            {
                tools.Add(_startJobProxy);
                continue;
            }

            if (_tools.TryGetValue(toolName, out var tool))
                tools.Add(tool);
        }

        return tools;
    }

    private async Task<string> BuildSystemPromptAsync(string userId, CancellationToken ct)
    {
        var prompt = _orchestratorOptions.SystemPrompt;

        // Inject active job context
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var activeJobs = await db.Jobs
            .Where(j => j.RequestedBy == userId && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running))
            .OrderByDescending(j => j.CreatedAt)
            .Take(10)
            .ToListAsync(ct);

        if (activeJobs.Count > 0)
        {
            var jobLines = activeJobs.Select(j =>
            {
                var line = $"- job {j.Id} — \"{j.Description}\" — status: {j.Status}";
                if (j.CurrentPhase is not null)
                    line += $" — phase: {j.CurrentPhase}";
                if (j.LastStatusMessage is not null)
                    line += $"\n  last update: \"{j.LastStatusMessage}\"";
                return line;
            });
            prompt += $"\n\nActive jobs for this user:\n{string.Join('\n', jobLines)}";
        }

        return prompt;
    }

    private async Task<ToolResult> ExecuteToolAsync(
        ToolUse toolUse, string userId, string? channelId, string? threadTs, CancellationToken ct)
    {
        if (toolUse.ToolName == "start_job")
            return await HandleStartJobAsync(toolUse, userId, channelId, threadTs, ct);

        if (!_tools.TryGetValue(toolUse.ToolName, out var tool))
            return new ToolResult(toolUse.Id, $"Unknown tool: {toolUse.ToolName}", IsError: true);

        if (!_options.AllowedTools.Contains(toolUse.ToolName))
            return new ToolResult(toolUse.Id, $"Tool '{toolUse.ToolName}' is not available in conversation mode.", IsError: true);

        _logger.LogDebug("Executing conversation tool {Tool}", toolUse.ToolName);
        return await tool.ExecuteAsync(toolUse.Input, ct);
    }

    private async Task<ToolResult> HandleStartJobAsync(
        ToolUse toolUse, string userId, string? channelId, string? threadTs, CancellationToken ct)
    {
        var description = toolUse.Input.TryGetProperty("description", out var desc)
            ? desc.GetString() ?? string.Empty
            : string.Empty;

        if (string.IsNullOrWhiteSpace(description))
            return new ToolResult(toolUse.Id, "Missing or empty 'description' field.", IsError: true);

        var job = await _jobQueue.EnqueueAsync(description, userId,
            channelId: channelId, threadTs: threadTs, ct: ct);
        _logger.LogInformation("ConversationHandler started job {JobId} for user {User}", job.Id, userId);

        return new ToolResult(toolUse.Id,
            $"Job `{job.Id}` has been queued. It will go through Research → Planning → Execution phases.");
    }

    private sealed class StartJobToolProxy : ITool
    {
        public string Name => "start_job";

        public string Description =>
            "Start an asynchronous job that will go through Research, Planning, and Execution phases. " +
            "Use this for any request that requires infrastructure changes, deployments, or long-running operations. " +
            "The job runs in the background and the user will receive status updates.";

        public JsonElement InputSchema => StartJobSchema;

        public Task<ToolResult> ExecuteAsync(JsonElement input, CancellationToken ct = default) =>
            throw new InvalidOperationException("StartJobToolProxy.ExecuteAsync should never be called directly.");
    }
}
