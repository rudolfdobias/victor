namespace Victor.Models;

public enum JobStatus { Queued, Running, Completed, Failed, Cancelled }

public class Job
{
    public Guid Id { get; set; }
    public string Description { get; set; } = string.Empty;
    public string RequestedBy { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public JobStatus Status { get; set; } = JobStatus.Queued;
    public string? ChannelId { get; set; }
    public string? ThreadTs { get; set; }
    public string? CurrentPhase { get; set; }
    public string? LastStatusMessage { get; set; }
    public string? Result { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
