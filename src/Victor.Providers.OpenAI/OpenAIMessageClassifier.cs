using Firefly.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI.Chat;
using Victor.Core.Abstractions;

namespace Victor.Providers.OpenAI;

[RegisterSingleton(Type = typeof(IMessageClassifier))]
public class OpenAIMessageClassifier : IMessageClassifier
{
    private const string SystemPrompt =
        """
        You are a message classifier. Given a user's chat message, determine if it is:
        - STANDALONE: a complete request that makes sense on its own (e.g. "restart the API", "what time is it", "save this key to vault")
        - FOLLOW_UP: a message that references prior conversation and needs history to understand (e.g. "try it again", "yes", "what about the other one", "do it", "same but for staging")

        Reply with exactly one word: STANDALONE or FOLLOW_UP
        """;

    private readonly ChatClient _chat;
    private readonly ILogger<OpenAIMessageClassifier> _logger;

    public OpenAIMessageClassifier(IOptions<OpenAIOptions> options, ILogger<OpenAIMessageClassifier> logger)
    {
        _logger = logger;
        var opts = options.Value;
        _chat = new ChatClient(opts.TriageModel, opts.ApiKey);
    }

    public async Task<bool> NeedsHistoryAsync(string message, CancellationToken ct = default)
    {
        try
        {
            var messages = new List<ChatMessage>
            {
                new SystemChatMessage(SystemPrompt),
                new UserChatMessage(message)
            };

            var response = await _chat.CompleteChatAsync(messages, new ChatCompletionOptions
            {
                MaxOutputTokenCount = 10
            }, ct);

            var result = response.Value.Content[0].Text.Trim().ToUpperInvariant();
            var needsHistory = result.Contains("FOLLOW");

            _logger.LogDebug("Triage for \"{Message}\": {Result} (needsHistory={NeedsHistory})",
                message.Length > 100 ? message[..100] + "..." : message, result, needsHistory);

            return needsHistory;
        }
        catch (Exception ex)
        {
            // If triage fails, default to not fetching history (standalone assumption)
            _logger.LogWarning(ex, "Triage classification failed, defaulting to standalone");
            return false;
        }
    }
}
