using System.Text.Json;
using Firefly.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pgvector;
using Victor.Core.Abstractions;
using Victor.Core.Models;

namespace Victor.Tools.Memory;

[RegisterScoped(Type = typeof(ITool))]
public class MemoryTool : ITool
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "action": {
                    "type": "string",
                    "enum": ["store", "recall"],
                    "description": "Whether to store a new memory or recall existing ones"
                },
                "task_id": {
                    "type": "string",
                    "description": "UUID of the current task (required for store)"
                },
                "category": {
                    "type": "string",
                    "description": "Category tag, e.g. 'incident', 'runbook', 'decision'"
                },
                "text": {
                    "type": "string",
                    "description": "Text to store or query to search for"
                }
            },
            "required": ["action", "text"]
        }
        """).RootElement;

    private readonly MemoryStore _store;
    private readonly IEmbeddingProvider _embedding;
    private readonly MemoryOptions _options;
    private readonly ILogger<MemoryTool> _logger;

    public string Name => "memory";
    public string Description => "Store and recall long-term memories using vector similarity search.";
    public JsonElement InputSchema => Schema;

    public MemoryTool(
        MemoryStore store,
        IEmbeddingProvider embedding,
        IOptions<MemoryOptions> options,
        ILogger<MemoryTool> logger)
    {
        _store = store;
        _embedding = embedding;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<ToolResult> ExecuteAsync(JsonElement input, CancellationToken ct = default)
    {
        var action = input.GetProperty("action").GetString();
        var text = input.GetProperty("text").GetString()
                   ?? throw new ArgumentException("Missing 'text' property");

        return action switch
        {
            "store" => await StoreAsync(input, text, ct),
            "recall" => await RecallAsync(text, ct),
            _ => new ToolResult(string.Empty, $"Unknown action: {action}", IsError: true)
        };
    }

    private async Task<ToolResult> StoreAsync(JsonElement input, string text, CancellationToken ct)
    {
        var taskId = input.TryGetProperty("task_id", out var tid)
            ? Guid.Parse(tid.GetString()!)
            : Guid.Empty;

        var category = input.TryGetProperty("category", out var cat)
            ? cat.GetString() ?? "general"
            : "general";

        var embeddingData = await _embedding.GetEmbeddingAsync(text, ct);

        var record = new MemoryRecord
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.UtcNow,
            TaskId = taskId,
            Category = category,
            Summary = text,
            Embedding = new Vector(embeddingData)
        };

        await _store.StoreAsync(record, ct);
        _logger.LogInformation("Stored memory {Id} in category '{Category}'", record.Id, category);

        return new ToolResult(string.Empty, $"Memory stored (id: {record.Id}).");
    }

    private async Task<ToolResult> RecallAsync(string query, CancellationToken ct)
    {
        var embeddingData = await _embedding.GetEmbeddingAsync(query, ct);
        var queryVector = new Vector(embeddingData);
        var results = await _store.RecallAsync(queryVector, _options.RecallTopK, ct);

        if (results.Count == 0)
            return new ToolResult(string.Empty, "No relevant memories found.");

        var lines = results.Select(r =>
            $"[{r.Timestamp:yyyy-MM-dd}] [{r.Category}] {r.Summary}");

        return new ToolResult(string.Empty, string.Join("\n", lines));
    }
}
