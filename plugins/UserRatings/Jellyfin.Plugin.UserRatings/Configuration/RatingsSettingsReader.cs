using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.UserRatings.Configuration;

/// <summary>
/// Reads <see cref="RatingsSettings"/> from the current configuration, at every use, so a saved change applies to the next
/// request without a restart. Each value it couldn't read is logged once at Warning for as long as the plugin runs.
/// </summary>
internal sealed class RatingsSettingsReader
{
    private readonly IPluginConfigurationProvider _configuration;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, byte> _logged = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="RatingsSettingsReader"/> class.
    /// </summary>
    /// <param name="configuration">The configuration.</param>
    /// <param name="logger">The logger; none by default.</param>
    public RatingsSettingsReader(IPluginConfigurationProvider configuration, ILogger<RatingsSettingsReader>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _configuration = configuration;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Reads the settings now.
    /// </summary>
    /// <returns>The settings.</returns>
    public RatingsSettings Read()
    {
        var settings = RatingsSettings.From(_configuration.GetConfiguration());
        foreach (var problem in settings.Problems)
        {
            if (_logged.TryAdd(problem, 0))
            {
                _logger.LogWarning("User Ratings setting: {Problem}", problem);
            }
        }

        return settings;
    }
}
