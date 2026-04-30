namespace Victor.Core.Abstractions;

/// <summary>
/// Lightweight classifier that determines whether an incoming message needs
/// conversation history to be understood. Uses a cheap/fast model.
/// </summary>
public interface IMessageClassifier
{
    /// <returns>True if the message is a follow-up that needs history context.</returns>
    Task<bool> NeedsHistoryAsync(string message, CancellationToken ct = default);
}
