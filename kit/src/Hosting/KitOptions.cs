using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Kit.Web.Pages;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kit.Hosting;

/// <summary>
/// The identity of the plugin that compiles the kit in. Each plugin creates one instance, passes it to
/// <see cref="KitAbiGuard.Register"/> and <see cref="KitServiceCollectionExtensions.AddKit"/>, and its tests check it
/// with <see cref="Validate"/> and use its prefixes for the API checks.
/// </summary>
internal sealed partial class KitOptions
{
    /// <summary>The logger category of kit code that runs without a <see cref="KitOptions"/>.</summary>
    public const string FallbackLoggerCategory = "Jellyfin.Plugin.Kit";

    /// <summary>
    /// Gets the display name, equal to build.yaml's <c>name</c> (for example <c>Bookmarks</c>).
    /// </summary>
    public required string PluginName { get; init; }

    /// <summary>
    /// Gets the API route prefix, which every controller route and controller class name starts with
    /// (for example <c>Bookmarks</c>).
    /// </summary>
    public required string RoutePrefix { get; init; }

    /// <summary>
    /// Gets the DOM, CSS and JS prefix (for example <c>jfbm</c>).
    /// </summary>
    public required string Prefix { get; init; }

    /// <summary>
    /// Gets the DTO and schema-id prefix every schema-visible type starts with (for example <c>Bookmark</c>).
    /// </summary>
    public required string SchemaPrefix { get; init; }

    /// <summary>
    /// Gets the data folder name under the server's data path (for example <c>bookmarks</c>).
    /// </summary>
    public required string DataFolder { get; init; }

    /// <summary>
    /// Gets the plugin's assembly.
    /// </summary>
    public required Assembly PluginAssembly { get; init; }

    /// <summary>
    /// Gets the plugin's id, equal to build.yaml's <c>guid</c> and <c>Plugin.Id</c>. Export envelopes carry it and an
    /// import refuses another plugin's. <see cref="Validate"/> requires it.
    /// </summary>
    public Guid PluginId { get; init; }

    /// <summary>
    /// Gets the plugin's <c>DeletedUserPolicy</c> setting, read each time a user is deleted and
    /// each time an admin applies it: what happens to a deleted user's records. Each plugin sets its default
    /// (<see cref="Users.DeletedUserPolicies.Parse"/> reads a setting string); the kit's default is
    /// <see cref="Users.DeletedUserPolicy.KeepAsOrphan"/>, which changes the least.
    /// </summary>
    public Func<IServiceProvider, Users.DeletedUserPolicy> DeletedUserPolicy { get; init; } = static _ => Users.DeletedUserPolicy.KeepAsOrphan;

    /// <summary>
    /// Gets a value indicating whether the plugin's settings page offers an admin <i>Start empty</i> for the list of users
    /// deleted on this server, <c>kit-users.json</c>, while it can't be read (<see cref="Users.UserReconciler.StartMarksEmptyAsync"/>;
    /// since the JSON store's hardening). It words the store's banner and the log: with a page, the admin is sent
    /// there; without, to fix or move the file with the server stopped. False by default.
    /// </summary>
    public bool UsersRecoveryPage { get; init; }

    /// <summary>
    /// Gets the clock of the kit's record lifecycle: the verify sweep's detach times, the detached records'
    /// retention and reattach, and the daily maintenance. Resolved once, when those services are created. The system clock
    /// by default; a developer build may move it on to check retention in a sandbox ("advance the clock (dev build
    /// flag)"), as Bookmarks' Debug build does with its setting <c>DeveloperClockOffsetDays</c>.
    /// </summary>
    public Func<IServiceProvider, TimeProvider> Clock { get; init; } = static _ => TimeProvider.System;

    /// <summary>
    /// Gets the plugin's <c>EnableWebUi</c> setting. The injection middleware reads it on every <c>index.html</c>
    /// request, with the request's services, so turning it off takes effect without a restart. Defaults to
    /// always on.
    /// </summary>
    public Func<IServiceProvider, bool> WebUiEnabled { get; init; } = static _ => true;

    /// <summary>
    /// Gets the optional head fragment: small inline CSS and script that the injection middleware
    /// inserts before the first <c>&lt;/head&gt;</c> of <c>index.html</c>, rendered on every request. The text must carry
    /// the marker attribute <c>data-&lt;Prefix&gt;-head</c> (<see cref="Web.IndexHtmlMarkers.Head"/>), which keeps it
    /// idempotent; text without it is not inserted. <c>index.html</c> is served without sign-in, so the fragment may only
    /// hold static text and server-wide values that say nothing about users or items.
    /// <see cref="Web.FallbackRouteHeadFragment"/> builds the one every plugin with fallback routes uses. <c>null</c>,
    /// or a delegate returning <c>null</c> or an empty string, inserts nothing, and the output is the same as without a
    /// fragment.
    /// </summary>
    public Func<HttpContext, string?>? HeadFragment { get; init; }

    /// <summary>
    /// Gets the plugin's user pages. Every page listed here is known: its shell
    /// <c>GET /&lt;RoutePrefix&gt;/Pages/&lt;key&gt;</c> answers 200, with a script-free "turned off" note while
    /// <see cref="KitPage.IsEnabled"/> is false, and only other keys answer 404. <c>AddKitPages</c> serves the shells and
    /// registers the enabled pages with Plugin Pages in <c>Auto</c>/<c>PluginPagesOnly</c> mode;
    /// <see cref="Web.FallbackRouteHeadFragment.Render"/> builds the head fragment of their fallback routes. Empty for
    /// a plugin without user pages.
    /// </summary>
    public IReadOnlyList<KitPage> Pages { get; init; } = [];

    /// <summary>
    /// Gets the plugin's <c>RegisterPluginPages</c> setting, read once at startup: whether the enabled pages are
    /// registered with Plugin Pages in <c>Auto</c>/<c>PluginPagesOnly</c> mode. Defaults to on.
    /// </summary>
    public Func<IServiceProvider, bool> RegisterPluginPages { get; init; } = static _ => true;

    /// <summary>
    /// Gets the plugin's <c>PageHostMode</c> setting, <c>FallbackOnly</c> (the default), <c>Auto</c> or
    /// <c>PluginPagesOnly</c>, parsed by <see cref="PageHostModes"/>. Read at startup for the Plugin Pages registration
    /// and per <c>index.html</c> for the browser (the head fragment carries it). <c>null</c> means the default.
    /// </summary>
    public Func<IServiceProvider, string?> PageHostMode { get; init; } = static _ => null;

    /// <summary>
    /// Gets the plugin's <c>ShowFallbackNav</c> setting: whether the plugin's links appear in the shared "Library
    /// tools" drawer section. Read per <c>index.html</c> (the head fragment carries it). Defaults to on.
    /// </summary>
    public Func<IServiceProvider, bool> ShowFallbackNav { get; init; } = static _ => true;

    /// <summary>
    /// Gets the plugin's <c>ReportWebAnchorProblems</c> setting: whether the ClientHealth endpoint
    /// records the reports browsers post when a jellyfin-web anchor is missing. Read per report; while it is off, reports
    /// are dropped (still 204) and nothing is logged. Browsers keep warning in their console either way. Defaults to on.
    /// </summary>
    public Func<IServiceProvider, bool> ReportWebAnchorProblems { get; init; } = static _ => true;

    /// <summary>
    /// Gets the plugin's Jellyfin Enhanced interop: the JE feature it replaces, for the overlap banner
    /// and the switch-off checklist, and its own shortcut settings, which the shortcut policy checks against jellyfin-web's
    /// and JE's keys. Used by <c>AddKitJellyfinEnhanced</c>; the default replaces nothing and has no shortcut.
    /// </summary>
    public Interop.JeInteropOptions JellyfinEnhanced { get; init; } = new();

    /// <summary>
    /// Gets the logger category of a kit component: <c>Jellyfin.Plugin.&lt;X&gt;.Kit.&lt;Component&gt;</c>, so kit log
    /// lines say which plugin they come from.
    /// </summary>
    /// <param name="component">The component, for example <c>AbiGuard</c>.</param>
    /// <returns>The category.</returns>
    public string GetLoggerCategory(string component)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(component);
        return $"{PluginAssembly.GetName().Name}.Kit.{component}";
    }

    /// <summary>
    /// Creates a logger for a kit component (<see cref="GetLoggerCategory"/>).
    /// </summary>
    /// <param name="loggerFactory">The logger factory.</param>
    /// <param name="component">The component.</param>
    /// <returns>The logger.</returns>
    public ILogger CreateLogger(ILoggerFactory loggerFactory, string component)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        return loggerFactory.CreateLogger(GetLoggerCategory(component));
    }

    /// <summary>
    /// Checks the values against the kit's naming rules. Plugins call it from a unit test; nothing checks at run
    /// time, because <c>RegisterServices</c> must never throw.
    /// </summary>
    /// <returns>The problems; empty when the options are valid.</returns>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(PluginName))
        {
            problems.Add("PluginName is empty.");
        }

        if (!PascalCaseName().IsMatch(RoutePrefix ?? string.Empty))
        {
            problems.Add($"RoutePrefix '{RoutePrefix}' must be a PascalCase name such as 'UserRatings'.");
        }

        if (!PascalCaseName().IsMatch(SchemaPrefix ?? string.Empty))
        {
            problems.Add($"SchemaPrefix '{SchemaPrefix}' must be a PascalCase name such as 'UserRatings'.");
        }

        if (!DomPrefix().IsMatch(Prefix ?? string.Empty))
        {
            problems.Add($"Prefix '{Prefix}' must be lower-case letters and digits starting with 'jf', such as 'jfur'.");
        }

        if (!FolderName().IsMatch(DataFolder ?? string.Empty))
        {
            problems.Add($"DataFolder '{DataFolder}' must be one lower-case folder name such as 'userratings'.");
        }

        if (PluginId.Equals(Guid.Empty))
        {
            problems.Add("PluginId is empty; set it to the plugin's id from build.yaml.");
        }

        if (DeletedUserPolicy is null)
        {
            problems.Add("DeletedUserPolicy has no delegate.");
        }

        var assemblyName = PluginAssembly?.GetName().Name;
        if (assemblyName is null || !assemblyName.StartsWith("Jellyfin.Plugin.", StringComparison.Ordinal))
        {
            problems.Add($"PluginAssembly '{assemblyName}' must be the plugin's Jellyfin.Plugin.<Name> assembly.");
        }

        problems.AddRange(ValidatePages());
        return problems;
    }

    private IEnumerable<string> ValidatePages()
    {
        var pages = Pages ?? [];
        foreach (var page in pages)
        {
            if (page is null)
            {
                yield return "Pages holds a null page.";
                continue;
            }

            if (!PageKey().IsMatch(page.Key ?? string.Empty))
            {
                yield return $"Page key '{page.Key}' must be lower-case letters, digits and '-', such as 'clips'.";
            }

            // The Plugin Pages registration of the page is checked the way the bridge checks it at startup.
            var registration = new PluginPageRegistration(page.PluginPagesId(RoutePrefix ?? string.Empty), page.ShellUrl(RoutePrefix ?? string.Empty), page.Title, page.Icon);
            foreach (var problem in registration.Validate())
            {
                yield return $"Page '{page.Key}': {problem}";
            }

            if (page.IsEnabled is null)
            {
                yield return $"Page '{page.Key}' has no IsEnabled delegate.";
            }
        }

        foreach (var duplicate in pages.Where(p => p is not null).GroupBy(p => p.Key, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            yield return $"Page key '{duplicate.Key}' is listed twice.";
        }

        foreach (var duplicate in pages.Where(p => p is not null).GroupBy(p => p.IdSuffix, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            yield return $"Page IdSuffix '{duplicate.Key}' is listed twice.";
        }

        if (pages.Count > 0 && !string.Equals(PluginAssembly?.GetName().Name, "Jellyfin.Plugin." + RoutePrefix, StringComparison.Ordinal))
        {
            yield return $"A plugin with user pages needs its assembly named Jellyfin.Plugin.{RoutePrefix}: the browser builds the Plugin Pages Ids from the route prefix.";
        }
    }

    [GeneratedRegex("^[A-Z][A-Za-z0-9]{1,39}$")]
    private static partial Regex PascalCaseName();

    [GeneratedRegex("^jf[a-z0-9]{2,8}$")]
    private static partial Regex DomPrefix();

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,39}$")]
    private static partial Regex FolderName();

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,39}$")]
    private static partial Regex PageKey();
}
