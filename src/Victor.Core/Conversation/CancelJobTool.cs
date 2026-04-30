using System.Text.Json;
using Firefly.DependencyInjection;
using Victor.Core.Abstractions;
using Victor.Core.Models;

namespace Victor.Core.Conversation;

[RegisterSingleton(Type = typeof(ITool))]
public class CancelJobTool : ITool
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "job_id": {
                    "type": "string",
                    "description": "The job ID (GUID) to cancel."
                }
            },
            "required": ["job_id"]
        }
        """).RootElement;

    private readonly JobQueue.JobQueue _jobQueue;

    public CancelJobTool(JobQueue.JobQueue jobQueue)
    {
        _jobQueue = jobQueue;
    }

    public string Name => "cancel_job";

    public string Description =>
        "Cancel a running or queued job. The job will be stopped and marked as cancelled.";

    public JsonElement InputSchema => Schema;

    public async Task<ToolResult> ExecuteAsync(JsonElement input, CancellationToken ct = default)
    {
        if (!input.TryGetProperty("job_id", out var jobIdProp) ||
            !Guid.TryParse(jobIdProp.GetString(), out var jobId))
            return new ToolResult("cancel_job", "Invalid or missing job_id.", IsError: true);

        var cancelled = await _jobQueue.CancelJobAsync(jobId, ct);
        return cancelled
            ? new ToolResult("cancel_job", $"Job `{jobId}` has been cancelled.")
            : new ToolResult("cancel_job", $"Job `{jobId}` not found or already finished.", IsError: true);
    }
}
