namespace Victor.Slack;

public class SlackOptions
{
    public const string SectionName = "slack";

    public required string BotToken { get; set; }
    public required string AppToken { get; set; }
    public required string DefaultChannelId { get; set; }
}
