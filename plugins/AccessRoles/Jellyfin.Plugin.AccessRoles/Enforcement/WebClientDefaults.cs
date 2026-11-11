using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Jellyfin.Plugin.AccessRoles.Enforcement;

/// <summary>
/// What jellyfin-web 10.11.5 takes for a <c>usersettings</c> key the server holds no value for,
/// so the display merge counts a key as changed only when the post's value differs from the stored one, an absent or empty
/// value counting as this default on either side. The web posts these keys on every save whether the user touched them or
/// not (a disabled field still has a value): <c>useEpisodeImagesInNextUpAndResume</c> is
/// <c>toBoolean(get(...), false).toString()</c>, so <c>"false"</c>; the skip lengths are
/// <c>parseInt(get(...) || "10000")</c> and <c>"30000"</c> (also the core's defaults for an absent key,
/// <c>v10.11.5:Jellyfin.Api/Controllers/DisplayPreferencesController.cs:155-166</c>); <c>dashboardTheme</c> is
/// <c>dashboardTheme() || defaultTheme.id</c> on both Display pages (legacy and experimental), the theme the web client's
/// <c>config.json</c> marks <c>default</c>, else its built-in <c>dark</c> (main bundle, <c>getThemes</c>). Home's keys have no
/// default: the server always answers its stored sections, and an absent <c>homesection</c> clears them.
/// </summary>
internal sealed class WebClientDefaults
{
    /// <summary>jellyfin-web's built-in default theme, and the stock <c>config.json</c>'s.</summary>
    public const string BuiltInTheme = "dark";

    private readonly Lazy<IReadOnlyDictionary<string, string>> _defaults;

    /// <summary>
    /// Initializes a new instance of the <see cref="WebClientDefaults"/> class.
    /// </summary>
    /// <param name="webPath">The folder of the web client the server hosts (<c>IApplicationPaths.WebPath</c>), or <c>null</c>.</param>
    public WebClientDefaults(string? webPath)
    {
        _defaults = new(() => Build(DefaultTheme(webPath)));
    }

    /// <summary>Gets the defaults of the stock web client (its <c>config.json</c> unread): the built-in theme.</summary>
    public static WebClientDefaults Stock { get; } = new(null);

    /// <summary>Gets the defaults by key, matched in any letter case.</summary>
    public IReadOnlyDictionary<string, string> Defaults => _defaults.Value;

    /// <summary>
    /// Gets the theme a web client's <c>config.json</c> marks <c>default</c> (its <c>themes</c> list, as jellyfin-web reads it),
    /// or <see cref="BuiltInTheme"/> when there is no such file, entry or id. Never throws.
    /// </summary>
    /// <param name="webPath">The web client's folder, or <c>null</c>.</param>
    /// <returns>The theme's id.</returns>
    public static string DefaultTheme(string? webPath)
    {
        if (string.IsNullOrEmpty(webPath))
        {
            return BuiltInTheme;
        }

        try
        {
            var file = Path.Combine(webPath, "config.json");
            if (!File.Exists(file))
            {
                return BuiltInTheme;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(file));
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("themes", out var themes)
                && themes.ValueKind == JsonValueKind.Array)
            {
                foreach (var theme in themes.EnumerateArray())
                {
                    if (theme.ValueKind == JsonValueKind.Object
                        && theme.TryGetProperty("default", out var isDefault) && isDefault.ValueKind == JsonValueKind.True
                        && theme.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                        && !string.IsNullOrEmpty(id.GetString()))
                    {
                        return id.GetString()!;
                    }
                }
            }
        }
        catch (Exception)
        {
            // jellyfin-web falls back to its built-in theme list when it can't read its config.json either.
        }

        return BuiltInTheme;
    }

    private static Dictionary<string, string> Build(string theme) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["useEpisodeImagesInNextUpAndResume"] = "false",
        ["dashboardTheme"] = theme,
        ["skipBackLength"] = "10000",
        ["skipForwardLength"] = "30000"
    };
}
