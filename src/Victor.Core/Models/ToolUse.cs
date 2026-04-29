using System.Text.Json;

namespace Victor.Core.Models;

public record ToolUse(string Id, string ToolName, JsonElement Input);

public record ToolResult(string ToolUseId, string Output, bool IsError = false);
