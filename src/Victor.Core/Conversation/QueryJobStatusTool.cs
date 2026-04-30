using System.Text.Json;
using Firefly.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Victor.Core.Abstractions;
using Victor.Core.Models;
using Victor.Models;

namespace Victor.Core.Conversation;

/// <summary>
/// Tool available to ConversationHandler. Queries the current status of a job by ID.
/// </summary>
[RegisterSingleton(Type = typeof(ITool))]
public class QueryJobStatusTool : ITool
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "job_id": {
                    "type": "string",
                    "description": "The job ID (GUID) to query. If omitted, returns all active jobs."
                }
            },
            "required": []
        }
        """).RootElement;

    private readonly IDbContextFactory<VictorDbContext> _dbFactory;

    public QueryJobStatusTool(IDbContextFactory<VictorDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public string Name => "query_job_status";

    public string Description =>
        "Query the current status of a job. Provide a job_id to check a specific job, " +
        "or omit it to see all active (queued or running) jobs.";

    public JsonElement InputSchema => Schema;

    public async Task<ToolResult> ExecuteAsync(JsonElement input, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        if (input.TryGetProperty("job_id", out var jobIdProp) &&
            Guid.TryParse(jobIdProp.GetString(), out var jobId))
        {
            var job = await db.Jobs.FindAsync([jobId], ct);
            if (job is null)
                return new ToolResult("query_job_status", $"Job `{jobId}` not found.");

            return new ToolResult("query_job_status", FormatJob(job));
        }

        // Return all active jobs
        var activeJobs = await db.Jobs
            .Where(j => j.Status == JobStatus.Queued || j.Status == JobStatus.Running)
            .OrderByDescending(j => j.CreatedAt)
            .Take(20)
            .ToListAsync(ct);

        if (activeJobs.Count == 0)
            return new ToolResult("query_job_status", "No active jobs.");

        var lines = activeJobs.Select(FormatJob);
        return new ToolResult("query_job_status", string.Join("\n\n", lines));
    }

    private static string FormatJob(Job job)
    {
        var parts = new List<string>
        {
            $"Job `{job.Id}`",
            $"  Description: {job.Description}",
            $"  Status: {job.Status}",
            $"  Requested by: {job.RequestedBy}",
            $"  Created: {job.CreatedAt:u}"
        };

        if (job.CurrentPhase is not null)
            parts.Add($"  Current phase: {job.CurrentPhase}");
        if (job.LastStatusMessage is not null)
            parts.Add($"  Last update: {job.LastStatusMessage}");
        if (job.Result is not null)
            parts.Add($"  Result: {job.Result}");
        if (job.Error is not null)
            parts.Add($"  Error: {job.Error}");
        if (job.CompletedAt is not null)
            parts.Add($"  Completed: {job.CompletedAt:u}");

        return string.Join('\n', parts);
    }
}
