using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SlackNet;

namespace Victor.Slack;

/// <summary>
/// Registers SlackNet's ISlackApiClient and binds SlackOptions.
/// Firefly can't do this — it's third-party library wiring.
/// </summary>
public static class SlackServiceExtensions
{
    public static IServiceCollection AddVictorSlack(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<SlackOptions>(configuration.GetSection(SlackOptions.SectionName));

        services.AddSingleton<ISlackApiClient>(sp =>
        {
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<SlackOptions>>();
            return new SlackApiClient(options.Value.BotToken);
        });

        services.AddHttpClient();

        return services;
    }
}
