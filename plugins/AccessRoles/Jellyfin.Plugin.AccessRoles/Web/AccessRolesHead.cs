using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.AccessRoles.Configuration;
using Jellyfin.Plugin.AccessRoles.Enforcement;
using Jellyfin.Plugin.AccessRoles.Storage;
using Jellyfin.Plugin.Kit.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.AccessRoles.Web;

/// <summary>
/// The web guard's head fragment, delivered through the kit's head fragment: inserted once before <c>&lt;/head&gt;</c>
/// of <c>index.html</c>, so it runs while the page is parsed, before jellyfin-web's deferred bundles and long before the
/// ClientScript.
/// <list type="bullet">
/// <item><description>a <c>&lt;style data-jfar-head&gt;</c> with the hide rules, keyed on classes of <c>&lt;html&gt;</c>
/// (<c>jfar-lock-&lt;section&gt;</c>, <c>jfar-lock-allsettings</c>, <c>jfar-pending</c>, <c>jfar-blocking</c>), built here
/// from the plugin's <c>Web/anchors.json</c>, so every selector is a declared anchor (the kit's anchor rules): one rule per
/// selector, so a selector a browser can't parse (<c>:has()</c>) drops only its own rule;</description></item>
/// <item><description>a <c>&lt;script data-jfar-head&gt;</c>: <c>window.__jfarHead</c> with the server-wide flags, then
/// the head script (<see cref="Script"/>), which sets those classes from the browser's own cache (the last signed-in user from
/// <c>localStorage.jellyfin_credentials</c>, their cached <c>/Me</c> answer), so nothing flickers.</description></item>
/// </list>
/// <c>index.html</c> is served without sign-in, so the fragment holds only server-wide values that say nothing about a
/// user: <c>anyManaged</c> (the first-load rule), whether the settings locks are enforced, and the <c>FirstLoadMode</c>,
/// <c>BlockedNavigationNotice</c> and <c>ShowManagedNoticeOnUserPages</c> settings. It never throws: the kit serves <c>index.html</c> without any of the plugin's web part when a fragment throws.
/// </summary>
internal static class AccessRolesHead
{
    /// <summary>The class prefix of a locked section on <c>&lt;html&gt;</c>.</summary>
    public const string LockClassPrefix = "jfar-lock-";

    /// <summary>The class on <c>&lt;html&gt;</c> while every settings section is locked.</summary>
    public const string AllSettingsClass = "jfar-lock-allsettings";

    /// <summary>The class on <c>&lt;html&gt;</c> while the first <c>/Me</c> of a user who may be managed is awaited (the first-load rule).</summary>
    public const string PendingClass = "jfar-pending";

    /// <summary>The class on <c>&lt;html&gt;</c> while a locked page is being redirected.</summary>
    public const string BlockingClass = "jfar-blocking";

    // The head script: plain ES5 in a function, so it runs in any browser jellyfin-web 10.11 supports.
    private const string HeadScript = """
        (function () {
            // Access Roles' head snippet, inline in index.html's <head> after window.__jfarHead (the
            // server-wide flags, Web/AccessRolesHead). It runs while the page is parsed, before jellyfin-web's deferred bundles,
            // and sets <html>'s classes from the browser's own cache so no locked entry point flickers: the last signed-in user
            // from jellyfin-apiclient's credential store, that user's cached /Me answer (jfar:rules:<user id>, written by the
            // ClientScript, Managed false too). index.html is anonymous: nothing per user comes from the server here.
            // The ClientScript's 30-rules.js applies the same rule (headClasses there; a test keeps both equal).
            'use strict';
            var H = window.__jfarHead || {};
            var root = document.documentElement;
            var PENDING_MS = 3000;
            H.t = typeof performance !== 'undefined' && performance.now ? Math.round(performance.now()) : 0;
            H.before = { readyState: document.readyState, events: !!window.Events, apiClient: !!window.ApiClient };
            H.classes = [];
            try {
                var uid = null;
                var creds = JSON.parse(window.localStorage.getItem('jellyfin_credentials') || '{}');
                var servers = (creds && creds.Servers) || [];
                var best = null;
                for (var i = 0; i < servers.length; i++) {
                    var s = servers[i];
                    if (s && s.UserId && s.AccessToken && (!best || (s.DateLastAccessed || 0) > (best.DateLastAccessed || 0))) {
                        best = s;
                    }
                }

                if (best) {
                    uid = String(best.UserId).replace(/-/g, '').toLowerCase();
                }

                var cached = null;
                if (uid) {
                    cached = JSON.parse(window.localStorage.getItem('jfar:rules:' + uid) || 'null');
                }

                H.userId = uid;
                H.cached = cached;
                if (H.enforced !== false && uid) {
                    if (cached && cached.Managed) {
                        var locked = cached.Locked || [];
                        for (var j = 0; j < locked.length; j++) {
                            H.classes.push('jfar-lock-' + String(locked[j]).toLowerCase().replace(/[^a-z]/g, ''));
                        }

                        if (cached.AllSettingsLocked) {
                            H.classes.push('jfar-lock-allsettings');
                        }
                    }

                    // The first-load rule: hide the entry points until /Me answers for a user known to be managed,
                    // or, without a cached answer, while the server manages anyone (HideUntilKnown, the default).
                    if (H.firstLoad !== 'ShowUntilKnown' && ((cached && cached.Managed) || (!cached && H.anyManaged))) {
                        H.classes.push('jfar-pending');
                    }
                }

                for (var k = 0; k < H.classes.length; k++) {
                    root.classList.add(H.classes[k]);
                }

                // 3 s at most even if the ClientScript never runs: the head lifts its own jfar-pending; the
                // ClientScript clears this timer when it starts and sets the class again for as long as it waits for /Me.
                if (H.classes.indexOf('jfar-pending') >= 0) {
                    H.pendingTimer = setTimeout(function () {
                        H.pendingTimer = null;
                        root.classList.remove('jfar-pending');
                    }, PENDING_MS);
                }
            } catch (e) {
                H.error = String(e);
            }

            window.__jfarHead = H;
        })();
        """;

    private static readonly Lazy<string> _css = new(() => BuildCss(WebAnchorCatalog.Load(typeof(AccessRolesHead).Assembly, typeof(WebAnchorCatalog).Assembly)));

    /// <summary>
    /// Gets which anchors each class on <c>&lt;html&gt;</c> hides (<c>display: none</c>): the hide rules. A section's
    /// class is <c>jfar-lock-</c> and the lowercase lock name; the dividers need several classes at once.
    /// </summary>
    public static IReadOnlyList<(string Classes, string Anchor)> HideRules { get; } =
    [
        (AllSettingsClass, "drawerSettings"),
        (AllSettingsClass, "userMenuSettings"),
        (LockClassPrefix + "profile", "userMenuProfile"),
        (LockClassPrefix + "profile", "settingsProfileLink"),
        (LockClassPrefix + "quickconnect", "userMenuQuickConnect"),
        (LockClassPrefix + "quickconnect", "settingsQuickConnectLink"),
        (LockClassPrefix + "displaydevice", "settingsDisplayLink"),
        (LockClassPrefix + "home", "settingsHomeLink"),
        (LockClassPrefix + "playback", "settingsPlaybackLink"),
        (LockClassPrefix + "subtitles", "settingsSubtitlesLink"),
        (LockClassPrefix + "controls", "settingsControlsLink"),
        (LockClassPrefix + "enhancedpanel", "jeEntries"),
        (AllSettingsClass + "." + LockClassPrefix + "profile." + LockClassPrefix + "enhancedpanel", "userMenuDivider"),
        (AllSettingsClass + "." + LockClassPrefix + "profile", "userMenuDividerNoJe")
    ];

    /// <summary>
    /// Gets the anchors <c>jfar-pending</c> keeps invisible (<c>visibility: hidden</c>) until <c>/Me</c> answers: every
    /// Settings entry point (the first-load rule; 3 s at most, then fail open: the head script lifts the class itself after 3 s,
    /// should the ClientScript never run, and the ClientScript takes it over when it starts).
    /// </summary>
    public static IReadOnlyList<string> PendingAnchors { get; } =
    [
        "drawerSettings", "userMenuProfile", "userMenuSettings", "userMenuQuickConnect", "jeEntries", "settingsProfileLink", "settingsDisplayLink",
        "settingsHomeLink", "settingsPlaybackLink", "settingsSubtitlesLink", "settingsControlsLink", "settingsQuickConnectLink"
    ];

    /// <summary>Gets the anchors <c>jfar-blocking</c> keeps invisible while a locked page is redirected.</summary>
    public static IReadOnlyList<string> BlockingAnchors { get; } = ["preferencePage"];

    /// <summary>
    /// Gets the head script. It lives here, not in <c>Web/</c>, because every script there is the ClientScript's (the
    /// repository's web conventions); the tests run it in the kit's fake page (<c>Web/head.test.mjs</c>).
    /// </summary>
    public static string Script => HeadScript;

    /// <summary>Gets the head's style sheet, built once from the embedded anchors.</summary>
    public static string Css => _css.Value;

    /// <summary>
    /// Renders the fragment for one <c>index.html</c> request. Never throws: a value that can't be read takes the safe side
    /// (<c>anyManaged</c> and <c>enforced</c> true), and anything else that fails gives no fragment (the guard then starts
    /// from <c>/Me</c> alone).
    /// </summary>
    /// <param name="services">The request's services.</param>
    /// <returns>The fragment, carrying the marker <c>data-jfar-head</c>, or <c>null</c>.</returns>
    public static string? Render(IServiceProvider services)
    {
        try
        {
            var flags = JsonSerializer.Serialize(Flags(services)).Replace("</", "<\\/", StringComparison.Ordinal);
            return "<style data-jfar-head>" + Css + "</style><script data-jfar-head>window.__jfarHead=" + flags + ";" + Script + "</script>";
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Gets the head's server-wide flags (from the plugin's settings).
    /// </summary>
    /// <param name="services">The server's services.</param>
    /// <returns><c>{anyManaged, enforced, firstLoad, notice, userPages}</c>.</returns>
    public static IDictionary<string, object?> Flags(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        PluginConfiguration? configuration = null;
        try
        {
            configuration = services.GetRequiredService<IPluginConfigurationProvider>().GetConfiguration();
        }
        catch (Exception)
        {
            // The defaults below.
        }

        bool enforced;
        try
        {
            enforced = (configuration?.EnforcementEnabled ?? true) && !File.Exists(services.GetRequiredService<AccessRolesPaths>().SafeModeFile);
        }
        catch (Exception)
        {
            enforced = true;
        }

        bool anyManaged;
        try
        {
            // A store that can't be trusted counts every user as possibly managed, as the filter does (fail closed).
            var snapshot = services.GetRequiredService<EffectiveRulesCache>().Current();
            anyManaged = snapshot.Managed > 0 || snapshot.DegradedBecause is not null;
        }
        catch (Exception)
        {
            anyManaged = true;
        }

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["anyManaged"] = anyManaged,
            ["enforced"] = enforced,
            ["firstLoad"] = string.Equals(configuration?.FirstLoadMode, "ShowUntilKnown", StringComparison.Ordinal) ? "ShowUntilKnown" : "HideUntilKnown",
            ["notice"] = string.Equals(configuration?.BlockedNavigationNotice, "Silent", StringComparison.Ordinal) ? "Silent" : "Toast",
            ["userPages"] = configuration?.ShowManagedNoticeOnUserPages ?? true
        };
    }

    /// <summary>
    /// Builds the hide rules from a catalog of anchors: one rule per selector of each anchor, every selector of an anchor
    /// (its fallbacks too, since a CSS rule costs nothing when it matches nothing).
    /// </summary>
    /// <param name="catalog">The kit's and the plugin's anchors.</param>
    /// <returns>The style sheet.</returns>
    /// <exception cref="InvalidOperationException">A rule names an anchor the catalog doesn't declare.</exception>
    public static string BuildCss(WebAnchorCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var css = new StringBuilder("/* Access Roles: hide rules keyed on the classes the head script and the guard set on the html element. */\n");
        foreach (var (classes, anchor) in HideRules)
        {
            Rules(css, catalog, "html." + classes, anchor, "display:none!important");
        }

        foreach (var anchor in PendingAnchors)
        {
            Rules(css, catalog, "html." + PendingClass, anchor, "visibility:hidden!important");
        }

        foreach (var anchor in BlockingAnchors)
        {
            Rules(css, catalog, "html." + BlockingClass, anchor, "visibility:hidden!important");
        }

        return css.ToString();
    }

    private static void Rules(StringBuilder css, WebAnchorCatalog catalog, string prefix, string anchor, string declaration)
    {
        var declared = catalog.Find(anchor) ?? throw new InvalidOperationException($"The head's rules name the anchor {anchor}, which Web/anchors.json doesn't declare.");
        foreach (var selector in declared.Css)
        {
            css.Append(prefix).Append(' ').Append(selector).Append('{').Append(declaration).Append("}\n");
        }
    }
}
