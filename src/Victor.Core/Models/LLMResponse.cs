namespace Victor.Core.Models;

public enum StopReason { EndTurn, ToolUse, MaxTokens }

public record LLMResponse(
    string Content,
    StopReason StopReason,
    IReadOnlyList<ToolUse>? ToolUses = null);
