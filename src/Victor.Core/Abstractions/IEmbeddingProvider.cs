namespace Victor.Core.Abstractions;

public interface IEmbeddingProvider
{
    Task<float[]> GetEmbeddingAsync(string text, CancellationToken ct = default);
}
