namespace Victor.Core.Orchestration;

/// <summary>
/// Posts a free-form question to the user during a running job and waits for their reply.
/// Used by the Orchestrator when the LLM needs clarification mid-flight.
/// </summary>
public interface IUserQueryGateway
{
    /// <param name="jobId">The job requesting user input.</param>
    /// <param name="question">The question to post to the user.</param>
    /// <param name="channelId">Channel to post in.</param>
    /// <param name="threadTs">Thread to post in (nullable).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The user's text reply, or null if timed out.</returns>
    Task<string?> AskAsync(string jobId, string question, string channelId, string? threadTs, CancellationToken ct = default);
}
