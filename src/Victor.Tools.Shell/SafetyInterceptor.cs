using Firefly.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Victor.Tools.Shell;

[RegisterSingleton]
public class SafetyInterceptor
{
    private readonly IReadOnlyList<string> _patterns;
    private readonly ILogger<SafetyInterceptor> _logger;

    public SafetyInterceptor(IOptions<ShellOptions> options, ILogger<SafetyInterceptor> logger)
    {
        _patterns = options.Value.SafetyPatterns;
        _logger = logger;
    }

    public bool RequiresApproval(string command)
    {
        foreach (var pattern in _patterns)
        {
            if (command.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Command matches safety pattern '{Pattern}': {Command}", pattern, command);
                return true;
            }
        }

        return false;
    }
}
