using Firefly.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI.Embeddings;
using Victor.Core.Abstractions;

namespace Victor.Providers.OpenAI;

[RegisterSingleton(Type = typeof(IEmbeddingProvider))]
public class OpenAIEmbeddingProvider : IEmbeddingProvider
{
    private readonly EmbeddingClient _client;
    private readonly int _dimensions;
    private readonly ILogger<OpenAIEmbeddingProvider> _logger;

    public OpenAIEmbeddingProvider(IOptions<OpenAIOptions> options, ILogger<OpenAIEmbeddingProvider> logger)
    {
        var opts = options.Value;
        _client = new EmbeddingClient(opts.EmbeddingModel, opts.ApiKey);
        _dimensions = opts.EmbeddingDimensions;
        _logger = logger;
    }

    public async Task<float[]> GetEmbeddingAsync(string text, CancellationToken ct = default)
    {
        _logger.LogDebug("Generating embedding for {Length} chars", text.Length);

        var opts = new EmbeddingGenerationOptions
        {
            Dimensions = _dimensions
        };

        var response = await _client.GenerateEmbeddingAsync(text, opts, ct);
        return response.Value.ToFloats().ToArray();
    }
}
