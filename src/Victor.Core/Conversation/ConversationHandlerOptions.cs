namespace Victor.Core.Conversation;

public class ConversationHandlerOptions
{
    public const string SectionName = "conversation";

    public int MaxToolIterations { get; set; } = 5;
    public List<string> AllowedTools { get; set; } = [];
}
