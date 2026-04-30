using System.Text.Json;
using Firefly.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Victor.Core.Abstractions;
using Victor.Core.JobQueue;
using Victor.Core.Models;
using Victor.Models;

namespace Victor.Core.Orchestration;

[RegisterSingleton]
public class Orchestrator
{
    private readonly ILLMProvider _llm;
    private readonly IReadOnlyDictionary<string, ITool> _tools;
    private readonly IApprovalGateway _approvalGateway;
    private readonly IJobStatusNotifier _statusNotifier;
    private readonly JobQueue.JobQueue _jobQueue;
    private readonly OrchestratorOptions _options;
    private readonly ILogger<Orchestrator> _logger;

    public Orchestrator(
        ILLMProvider llm,
        IEnumerable<ITool> tools,
        IApprovalGateway approvalGateway,
        IJobStatusNotifier statusNotifier,
        JobQueue.JobQueue jobQueue,
        IOptions<OrchestratorOptions> options,
        ILogger<Orchestrator> logger)
    {
        _llm = llm;
        _tools = tools.ToDictionary(t => t.Name, t => t);
        _approvalGateway = approvalGateway;
        _statusNotifier = statusNotifier;
        _jobQueue = jobQueue;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<string> RunAsync(
        Job job,
        IReadOnlyList<Message>? conversationHistory = null,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Starting job {JobId}: {Description}", job.Id, job.Description);

        var conversation = new List<Message>();

        // Seed with conversation history if available (Slack context)
        if (conversationHistory is { Count: > 0 })
        {
            conversation.AddRange(conversationHistory);
            _logger.LogDebug("Seeded {Count} history messages for job {JobId}",
                conversationHistory.Count, job.Id);
        }
        else
        {
            conversation.Add(new Message(Role.User, job.Description));
        }

        var phaseResults = new List<string>();
        AskUserTool.SetCurrentJob(job);

        foreach (var phase in new[] { _options.Research, _options.Planning, _options.Execution })
        {
            _logger.LogInformation("Entering phase {Phase} for job {JobId}", phase.Type, job.Id);

            job.CurrentPhase = phase.Type.ToString();
            job.LastStatusMessage = $"Starting {phase.Type} phase";
            await _jobQueue.UpdateJobAsync(job, ct);
            await _statusNotifier.NotifyPhaseStartedAsync(job, phase.Type, ct);

            var result = await RunPhaseAsync(job, phase, conversation, ct);
            phaseResults.Add(result);

            job.LastStatusMessage = $"{phase.Type} phase complete";
            await _jobQueue.UpdateJobAsync(job, ct);
            await _statusNotifier.NotifyPhaseCompletedAsync(job, phase.Type, result, ct);

            // Feed phase summary into the next phase's context
            conversation.Add(new Message(Role.Assistant, result));
            conversation.Add(new Message(Role.User, $"Phase {phase.Type} complete. Proceed to next phase."));
        }

        AskUserTool.SetCurrentJob(null);
        return phaseResults[^1]; // Return Execution phase result
    }

    private async Task<string> RunPhaseAsync(
        Job job,
        PhaseConfig phase,
        List<Message> conversation,
        CancellationToken ct)
    {
        var phaseTools = _tools.Values
            .Where(t => phase.AllowedTools.Contains(t.Name))
            .ToList();

        var systemPrompt = $"{_options.SystemPrompt}\n\n## Current Phase: {phase.Type}\n" +
                           $"Available tools: {string.Join(", ", phase.AllowedTools)}";

        var messages = new List<Message>(conversation);

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var request = new LLMRequest(systemPrompt, messages, phaseTools);
            var response = await _llm.CompleteAsync(request, ct);

            if (response.StopReason == StopReason.EndTurn || response.ToolUses is null or { Count: 0 })
            {
                _logger.LogInformation("Phase {Phase} complete for job {JobId}", phase.Type, job.Id);
                return response.Content;
            }

            // Process tool calls
            messages.Add(new Message(Role.Assistant, response.Content));

            foreach (var toolUse in response.ToolUses)
            {
                var result = await ExecuteToolAsync(job, phase, toolUse, ct);
                // Append tool result as a user message (provider implementations format this appropriately)
                messages.Add(new Message(Role.User, $"[Tool: {toolUse.ToolName}] {result.Output}"));
            }
        }
    }

    private async Task<ToolResult> ExecuteToolAsync(
        Job job,
        PhaseConfig phase,
        ToolUse toolUse,
        CancellationToken ct)
    {
        if (!_tools.TryGetValue(toolUse.ToolName, out var tool))
            return new ToolResult(toolUse.Id, $"Unknown tool: {toolUse.ToolName}", IsError: true);

        // Safety check
        var commandStr = toolUse.Input.ToString() ?? string.Empty;
        if (RequiresApproval(phase, commandStr))
        {
            _logger.LogWarning("Tool {Tool} requires approval for job {JobId}", toolUse.ToolName, job.Id);
            var approved = await _approvalGateway.RequestApprovalAsync(
                job.Id.ToString(), toolUse.ToolName, commandStr, ct);

            if (!approved)
            {
                _logger.LogWarning("Tool {Tool} rejected for job {JobId}", toolUse.ToolName, job.Id);
                return new ToolResult(toolUse.Id, "Command rejected by operator.", IsError: true);
            }
        }

        _logger.LogInformation("Executing tool {Tool} for job {JobId}", toolUse.ToolName, job.Id);

        job.LastStatusMessage = $"Executing {toolUse.ToolName}";
        await _jobQueue.UpdateJobAsync(job, ct);

        return await tool.ExecuteAsync(toolUse.Input, ct);
    }

    private bool RequiresApproval(PhaseConfig phase, string command) =>
        phase.SafetyPatterns.Any(pattern =>
            command.Contains(pattern, StringComparison.OrdinalIgnoreCase));
}
