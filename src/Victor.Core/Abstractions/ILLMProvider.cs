using Victor.Core.Models;

namespace Victor.Core.Abstractions;

public interface ILLMProvider
{
    string Name { get; }
    Task<LLMResponse> CompleteAsync(LLMRequest request, CancellationToken ct = default);
    IAsyncEnumerable<string> StreamAsync(LLMRequest request, CancellationToken ct = default);
}
