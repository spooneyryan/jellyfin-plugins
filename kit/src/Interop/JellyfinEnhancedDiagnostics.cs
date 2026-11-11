using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.Kit.Hosting;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Kit.Interop;

/// <summary>
/// The Diagnostics entry <c>JellyfinEnhanced</c>, described on every request: JE's state and the
/// settings the kit reads, its effective shortcuts and every user's own, Custom Tabs' tabs, Plugin Pages' list, the
/// overlap (banner, checklist, "Use B") of <see cref="JeInteropOptions.Overlap"/>, and the policy's check of each of the
/// plugin's own shortcut settings. The settings page renders the banner, the warnings and the checklist from it.
/// </summary>
internal sealed class JellyfinEnhancedDiagnostics
{
    private readonly KitOptions _options;
    private readonly JellyfinEnhancedReader _reader;
    private readonly ShortcutPolicy _policy;
    private readonly IServiceProvider _services;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyfinEnhancedDiagnostics"/> class.
    /// </summary>
    /// <param name="options">The plugin's kit options.</param>
    /// <param name="reader">The reader.</param>
    /// <param name="policy">The shortcut policy.</param>
    /// <param name="services">The server's services (the plugin's settings, the user manager for names).</param>
    public JellyfinEnhancedDiagnostics(KitOptions options, JellyfinEnhancedReader reader, ShortcutPolicy policy, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(services);
        _options = options;
        _reader = reader;
        _policy = policy;
        _services = services;
    }

    /// <summary>
    /// Describes the overlap only (the banner, checklist and "Use B"), for a plugin's own use.
    /// </summary>
    /// <returns>The state.</returns>
    public JeOverlapState Overlap() => JeSwitchOff.Describe(_options.JellyfinEnhanced.Overlap, _reader.Read(), UserName);

    /// <summary>
    /// Describes the entry.
    /// </summary>
    /// <returns>The value, dictionaries and lists for the Diagnostics body.</returns>
    public IDictionary<string, object?> Describe()
    {
        var snapshot = _reader.Read();
        var settings = snapshot.Settings;
        var overlap = JeSwitchOff.Describe(_options.JellyfinEnhanced.Overlap, snapshot, UserName);
        var problems = new List<string>();
        if (settings.Problem is not null)
        {
            problems.Add(settings.Problem);
        }

        if (snapshot.UsersProblem is not null)
        {
            problems.Add("Users' shortcuts: " + snapshot.UsersProblem);
        }

        problems.AddRange(snapshot.Users.Where(u => u.Problem is not null).Select(u => $"{u.UserId:N}/shortcuts.json: {u.Problem}"));
        if (snapshot.CustomTabs.Problem is not null)
        {
            problems.Add("Custom Tabs: " + snapshot.CustomTabs.Problem);
        }

        if (snapshot.PluginPages.Problem is not null)
        {
            problems.Add("Plugin Pages: " + snapshot.PluginPages.Problem);
        }

        var checks = ReadOurShortcuts().Select(setting => _policy.Check(setting, snapshot)).ToList();
        var showBanner = ShowBanner();
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Installed"] = snapshot.Plugin.Installed,
            ["Active"] = snapshot.Plugin.Active,
            ["Version"] = snapshot.Plugin.Version,
            ["Status"] = snapshot.Plugin.Status,
            ["SettingsFile"] = settings.Exists,
            // False while the file is there but can't be read: Settings then holds JE's defaults, not its values.
            ["SettingsReadable"] = settings.Problem is null,
            ["Settings"] = JellyfinEnhancedDefaults.Flags.Keys.ToDictionary(name => name, settings.Flag, StringComparer.Ordinal),
            ["Shortcuts"] = JeShortcuts.EffectiveKeys(settings).ToDictionary(p => p.Key, p => (object?)p.Value, StringComparer.Ordinal),
            ["UserShortcuts"] = snapshot.Users.Select(user => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["UserId"] = user.UserId.ToString("N", CultureInfo.InvariantCulture),
                ["Name"] = UserName(user.UserId),
                ["Overrides"] = JeShortcuts.UserOverrides(user).ToDictionary(p => p.Key, p => (object?)p.Value, StringComparer.Ordinal),
                ["Problem"] = user.Problem
            }).ToList(),
            ["CustomTabs"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["Installed"] = snapshot.CustomTabs.Installed,
                ["Active"] = snapshot.CustomTabs.Active,
                ["Tabs"] = snapshot.CustomTabs.Tabs.Select(tab => new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["Title"] = tab.Title,
                    ["JellyfinEnhancedBookmarks"] = string.Equals(tab.ContentHtml.Trim(), JellyfinEnhancedFiles.BookmarksTabHtml, StringComparison.Ordinal)
                }).ToList()
            },
            ["PluginPages"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["Installed"] = snapshot.PluginPages.Installed,
                ["Pages"] = snapshot.PluginPages.Pages.Select(page => new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["Id"] = page.Id,
                    ["Url"] = page.Url,
                    ["DisplayText"] = page.DisplayText
                }).ToList()
            },
            ["Problems"] = problems,
            ["Overlap"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["Feature"] = overlap.Feature.ToString(),
                ["On"] = overlap.FeatureOn,
                ["Banner"] = showBanner ? overlap.Banner : null,
                ["BannerHidden"] = !showBanner && overlap.Banner is not null,
                ["Complete"] = overlap.Complete,
                ["CanUseB"] = overlap.CanUseB,
                ["UseBNote"] = overlap.UseBNote,
                ["Checklist"] = overlap.Checklist.Select(item => new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["Key"] = item.Key,
                    ["Text"] = item.Text,
                    ["Done"] = item.Done,
                    ["Detail"] = item.Detail,
                    ["NeedsRestart"] = item.NeedsRestart,
                    ["Optional"] = item.Optional,
                    ["Unknown"] = item.Unknown
                }).ToList()
            },
            ["OurShortcuts"] = checks.Select(DescribeCheck).ToList(),
            // Every user's own included: this entry is the admin's (RequiresElevation); a capabilities response gets
            // ReservedCombos(userId) instead.
            ["ReservedCombos"] = _policy.AllReservedCombos(snapshot)
        };
    }

    /// <summary>
    /// Checks a value for one of the plugin's shortcut settings before it is saved (since Bookmarks 1.1): the
    /// settings page's field checks what the admin types against jellyfin-web's and Jellyfin Enhanced's keys as JE has them
    /// now, with the same policy as the saved setting's <c>OurShortcuts</c> entry, and saves the normalised combination.
    /// </summary>
    /// <param name="name">The setting's name (<see cref="JeShortcutSetting.Name"/>), such as <c>BookmarkNow</c>.</param>
    /// <param name="combo">The text to check; <c>null</c> or empty is the shortcut turned off.</param>
    /// <returns>The check, as an <c>OurShortcuts</c> entry describes one; <c>null</c> when the plugin has no shortcut
    /// setting of that name.</returns>
    public IDictionary<string, object?>? CheckShortcut(string name, string? combo)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var setting = ReadOurShortcuts().FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal));
        return setting is null ? null : DescribeCheck(_policy.Check(setting with { Combo = combo }));
    }

    private static Dictionary<string, object?> DescribeCheck(ShortcutCheck check) => new(StringComparer.Ordinal)
    {
        ["Name"] = check.Name,
        ["Label"] = check.Label,
        ["Combo"] = check.Combo,
        ["Valid"] = check.IsValid,
        ["Reserved"] = check.IsReserved,
        ["Warning"] = check.Warning,
        ["Reservations"] = check.Reservations.Select(r => new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Combo"] = r.Combo,
            ["Owner"] = r.Owner.ToString(),
            ["Description"] = r.Description,
            ["UserId"] = r.UserId?.ToString("N", CultureInfo.InvariantCulture),
            ["Default"] = r.IsDefault
        }).ToList()
    };

    private bool ShowBanner()
    {
        try
        {
            return _options.JellyfinEnhanced.ShowBanner(_services);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // The plugin's settings couldn't be read: the banner shows, as by default.
            return true;
        }
    }

    private IReadOnlyList<JeShortcutSetting> ReadOurShortcuts()
    {
        try
        {
            return _options.JellyfinEnhanced.Shortcuts(_services);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // The plugin's settings couldn't be read: no shortcut to check.
            return [];
        }
    }

    private string? UserName(Guid userId)
    {
        try
        {
            return _services.GetService<IUserManager>()?.GetUserById(userId)?.Username;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or ObjectDisposedException)
        {
            return null;
        }
    }
}
