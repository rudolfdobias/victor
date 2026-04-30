namespace Victor.Providers.OpenAI;

public class OpenAIOptions
{
    public const string SectionName = "llm:openai";

    public required string ApiKey { get; set; }
    public string Model { get; set; } = "gpt-4o";
    public string TriageModel { get; set; } = "gpt-4o-mini";
    public string EmbeddingModel { get; set; } = "text-embedding-3-small";
    public int EmbeddingDimensions { get; set; } = 1536;
}
