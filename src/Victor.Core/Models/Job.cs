namespace Victor.Core.Models;

public enum JobStatus { Queued, Running, Completed, Failed }

public class Job
{
    public Guid Id { get; set; }
    public string Description { get; set; } = string.Empty;
    public string RequestedBy { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public JobStatus Status { get; set; } = JobStatus.Queued;
    public string? Result { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
