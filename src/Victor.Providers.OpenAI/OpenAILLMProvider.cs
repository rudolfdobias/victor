using System.ClientModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Firefly.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI.Chat;
using Victor.Core.Abstractions;
using Victor.Core.Models;
using ChatMessage = OpenAI.Chat.ChatMessage;

namespace Victor.Providers.OpenAI;

[RegisterSingleton(Type = typeof(ILLMProvider))]
public class OpenAILLMProvider : ILLMProvider
{
    private readonly ChatClient _chat;
    private readonly ILogger<OpenAILLMProvider> _logger;

    public string Name => "openai";

    public OpenAILLMProvider(IOptions<OpenAIOptions> options, ILogger<OpenAILLMProvider> logger)
    {
        _logger = logger;
        var opts = options.Value;
        _chat = new ChatClient(opts.Model, opts.ApiKey);
    }

    public async Task<LLMResponse> CompleteAsync(LLMRequest request, CancellationToken ct = default)
    {
        var messages = ToOpenAIMessages(request);
        var chatOptions = BuildOptions(request);

        _logger.LogDebug("OpenAI request: {MessageCount} messages, {ToolCount} tools",
            messages.Count, request.Tools?.Count ?? 0);

        var response = await _chat.CompleteChatAsync(messages, chatOptions, ct);
        var completion = response.Value;

        return MapResponse(completion);
    }

    public async IAsyncEnumerable<string> StreamAsync(
        LLMRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var messages = ToOpenAIMessages(request);
        var chatOptions = BuildOptions(request);

        AsyncCollectionResult<StreamingChatCompletionUpdate> stream =
            _chat.CompleteChatStreamingAsync(messages, chatOptions, ct);

        await foreach (var update in stream)
        {
            foreach (var part in update.ContentUpdate)
            {
                if (part.Text is { } text)
                    yield return text;
            }
        }
    }

    private static List<ChatMessage> ToOpenAIMessages(LLMRequest request)
    {
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(request.SystemPrompt)
        };

        foreach (var msg in request.Messages)
        {
            messages.Add(msg.Role switch
            {
                Role.User => new UserChatMessage(msg.Content),
                Role.Assistant => new AssistantChatMessage(msg.Content),
                _ => throw new ArgumentOutOfRangeException(nameof(msg.Role))
            });
        }

        return messages;
    }

    private static ChatCompletionOptions BuildOptions(LLMRequest request)
    {
        var options = new ChatCompletionOptions
        {
            MaxOutputTokenCount = request.MaxTokens,
        };

        if (request.Tools is { Count: > 0 })
        {
            foreach (var tool in request.Tools)
            {
                options.Tools.Add(ChatTool.CreateFunctionTool(
                    tool.Name,
                    tool.Description,
                    BinaryData.FromString(tool.InputSchema.GetRawText())));
            }
        }

        return options;
    }

    private static LLMResponse MapResponse(ChatCompletion completion)
    {
        var content = string.Concat(completion.Content
            .Where(p => p.Kind == ChatMessageContentPartKind.Text)
            .Select(p => p.Text));

        var toolUses = completion.ToolCalls?
            .Select(tc => new ToolUse(
                tc.Id,
                tc.FunctionName,
                JsonDocument.Parse(tc.FunctionArguments).RootElement))
            .ToList();

        var stopReason = completion.FinishReason switch
        {
            ChatFinishReason.ToolCalls => StopReason.ToolUse,
            ChatFinishReason.Length => StopReason.MaxTokens,
            _ => StopReason.EndTurn,
        };

        return new LLMResponse(content, stopReason, toolUses);
    }
}
