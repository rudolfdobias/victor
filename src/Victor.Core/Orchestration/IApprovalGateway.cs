namespace Victor.Core.Orchestration;

/// <summary>Requests human approval before executing a sensitive command.</summary>
public interface IApprovalGateway
{
    /// <returns>True if approved, false if rejected or timed out.</returns>
    Task<bool> RequestApprovalAsync(string jobId, string toolName, string command, CancellationToken ct = default);
}
