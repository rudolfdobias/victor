using Victor.Core.Abstractions;

namespace Victor.Core.Models;

public record LLMRequest(
    string SystemPrompt,
    IReadOnlyList<Message> Messages,
    IReadOnlyList<ITool>? Tools = null,
    int MaxTokens = 4096);
