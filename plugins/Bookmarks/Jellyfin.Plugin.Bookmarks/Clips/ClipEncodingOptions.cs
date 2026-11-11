using System;
using System.Linq;
using Jellyfin.Plugin.Bookmarks.Configuration;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// Validated encoder settings for accurate (re-encoded) clips.
/// </summary>
internal sealed record ClipEncodingOptions
{
    private const string DefaultPreset = "veryfast";

    private static readonly string[] _presets =
        ["ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow"];

    /// <summary>
    /// Gets the x264 preset.
    /// </summary>
    public string Preset { get; init; } = DefaultPreset;

    /// <summary>
    /// Gets the x264 constant rate factor.
    /// </summary>
    public int Crf { get; init; } = 20;

    /// <summary>
    /// Gets the maximum output height in pixels; zero keeps the source height.
    /// </summary>
    public int MaxHeight { get; init; }

    /// <summary>
    /// Gets the AAC bitrate in kbit/s.
    /// </summary>
    public int AudioBitrateKbps { get; init; } = 192;

    /// <summary>
    /// Gets the number of output audio channels.
    /// </summary>
    public int AudioChannels { get; init; } = 2;

    /// <summary>
    /// Builds options from the plugin configuration, replacing out-of-range values with safe ones.
    /// </summary>
    /// <param name="configuration">The plugin configuration.</param>
    /// <returns>The validated options.</returns>
    public static ClipEncodingOptions FromConfiguration(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var preset = _presets.FirstOrDefault(p => string.Equals(p, configuration.VideoPreset?.Trim(), StringComparison.OrdinalIgnoreCase));

        return new ClipEncodingOptions
        {
            Preset = preset ?? DefaultPreset,
            Crf = Math.Clamp(configuration.VideoCrf, 0, 51),
            MaxHeight = Math.Max(0, configuration.MaxVideoHeight),
            AudioBitrateKbps = Math.Clamp(configuration.AudioBitrateKbps, 32, 640),
            AudioChannels = Math.Clamp(configuration.AudioChannels, 1, 8)
        };
    }
}
