using System.Text.Json;
using Victor.Core.Models;

namespace Victor.Core.Abstractions;

public interface ITool
{
    string Name { get; }
    string Description { get; }
    JsonElement InputSchema { get; }
    Task<ToolResult> ExecuteAsync(JsonElement input, CancellationToken ct = default);
}
