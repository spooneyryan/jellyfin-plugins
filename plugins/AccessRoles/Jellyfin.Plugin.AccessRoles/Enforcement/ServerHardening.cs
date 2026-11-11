using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.AccessRoles.Configuration;
using Jellyfin.Plugin.AccessRoles.Model;
using Jellyfin.Plugin.AccessRoles.Rules;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Library;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Search;
using MediaBrowser.Model.Session;
using MediaBrowser.Model.System;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.AccessRoles.Enforcement;

/// <summary>
/// The admin-surface, server-wide and hardening rules (<see cref="RuleOwner.ServerHardening"/>), applied by <see cref="AccessRulesActionFilter"/>
/// to every caller that is neither an administrator nor an API key (they are never restricted): the <c>AdminSurfaces</c> and
/// <c>UserDirectory</c> locks of the caller's roles; the server-wide configuration reads, restart block and public user
/// list (the last two for anonymous callers too, whom the core admits on the LAN, as measured on 10.11.5); and the server-wide library
/// leak hardening (<c>LibraryHardening</c> <c>Standard</c>, for every signed-in non-admin, managed or not).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Visibility</b> is the kit's <see cref="LibraryAccess.Visibility"/>: <c>GetItemById&lt;BaseItem&gt;(id, user)</c>,
/// a named view judged by its <c>DisplayParentId</c>, an owned item by its owner's library, and an id
/// that names nothing passes (the core then finds nothing either). Each id is looked up once per request.</item>
/// <item><b>Answers</b>: a refusal is 403 <c>urn:jellyfin-plugin-kit:locked</c> (never <c>ParentalControl</c>); a hidden
/// item is an empty result or 404 (never the core's 401 that names a library), answered before the action, or the
/// result rewritten after it, always on new objects: the server's live configuration, sessions and users are never
/// changed.</item>
/// <item><b>Failure</b>: a rule that throws refuses the request (fail closed, as for the settings
/// rules, the restart and configuration rows and the hardening alike). The filter counts it
/// and shows it in Diagnostics.</item>
/// <item><b>The People search</b> keeps the names with an item in the caller's libraries, one database query per page
/// (<see cref="IPeopleLibraries"/>).</item>
/// <item><b>Not applied</b>: the <c>Strict</c> kinds (not done yet) and <see cref="RuleKind.BackstopExempt"/>.</item>
/// </list>
/// </remarks>
internal sealed class ServerHardening
{
    /// <summary>The detail of a refusal under the <c>AdminSurfaces</c> lock.</summary>
    public const string AdminSurfacesMessage = "The Dashboard's data and server calls are managed by your server administrator, so this isn't allowed.";

    /// <summary>The detail of a refused configuration read.</summary>
    public const string ConfigurationMessage = "This server's configuration is managed by your server administrator, so it can't be read.";

    /// <summary>The detail of a refused restart.</summary>
    public const string RestartMessage = "Only an administrator can restart this server.";

    /// <summary>The detail of a request refused because a rule couldn't decide (fail closed).</summary>
    public const string UnavailableMessage = "This request is temporarily unavailable: Access Roles couldn't check it, so it was refused. Try again in a moment.";

    /// <summary>The answer of a user that isn't the caller under <c>UserDirectory</c>, the core's own for a user it doesn't find.</summary>
    public const string UserNotFound = "User not found";

    private const string UserIdArgument = "userId";

    private static readonly MethodInfo _memberwiseClone = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private readonly IUserManager _userManager;
    private readonly LibraryAccess _access;
    private readonly IPeopleLibraries? _people;
    private readonly ConfigurationReadGuard _configuration;
    private readonly BlockCounter _counter;

    /// <summary>
    /// Initializes a new instance of the <see cref="ServerHardening"/> class.
    /// </summary>
    /// <param name="userManager">The user manager, for the caller's user.</param>
    /// <param name="access">The kit's library access over the server's library manager.</param>
    /// <param name="people">The People-search query; <c>null</c> without the server's database (the rule then refuses).</param>
    /// <param name="configuration">The configuration copies.</param>
    /// <param name="counter">The blocked-request counter.</param>
    public ServerHardening(IUserManager userManager, LibraryAccess access, IPeopleLibraries? people, ConfigurationReadGuard configuration, BlockCounter counter)
    {
        ArgumentNullException.ThrowIfNull(userManager);
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(counter);
        _userManager = userManager;
        _access = access;
        _people = people;
        _configuration = configuration;
        _counter = counter;
    }

    /// <summary>
    /// Gets the rule name a rule is counted and timed under: <c>Locks.AdminSurfaces</c>, <c>Locks.UserDirectory</c>,
    /// <c>ServerWide.ConfigurationReads</c>, <c>ServerWide.Restart</c>, <c>ServerWide.PublicUsers</c>,
    /// <c>ServerWide.SystemInfo</c> (since 0.9.9 it blanks the paths for every non-admin under
    /// <c>LibraryHardening</c> <c>Standard</c>, not only under the lock), or
    /// <c>Hardening.&lt;kind&gt;</c> (<c>Hardening.Items</c>, <c>Hardening.GenericBackstop</c>).
    /// </summary>
    /// <param name="rule">The rule.</param>
    /// <returns>The name.</returns>
    public static string RuleName(ActionManifestRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return rule.Kind switch
        {
            RuleKind.LockedReject => "Locks." + rule.Lock,
            RuleKind.SystemInfoBlank => "ServerWide.SystemInfo",
            RuleKind.UserDirectory => "Locks." + AccessRolesLock.UserDirectory,
            RuleKind.ConfigurationReadGuard => "ServerWide.ConfigurationReads",
            RuleKind.RestartBlock => "ServerWide.Restart",
            RuleKind.PublicUsersTrim => "ServerWide.PublicUsers",
            _ => "Hardening." + (rule.Kind.ToString().StartsWith("Harden", StringComparison.Ordinal) ? rule.Kind.ToString()["Harden".Length..] : rule.Kind.ToString())
        };
    }

    /// <summary>
    /// Gets whether a rule applies to anonymous callers too (the restart block and the public user list).
    /// </summary>
    /// <param name="kind">The rule's kind.</param>
    /// <returns><c>true</c> for one.</returns>
    public static bool CoversAnonymousCallers(RuleKind kind) => kind is RuleKind.RestartBlock or RuleKind.PublicUsersTrim;

    /// <summary>
    /// Gets whether a rule is the library leak hardening's (<c>LibraryHardening</c>).
    /// </summary>
    /// <param name="kind">The rule's kind.</param>
    /// <returns><c>true</c> for one.</returns>
    public static bool IsLibraryHardening(RuleKind kind) => kind is RuleKind.HardenLatest or RuleKind.HardenItems or RuleKind.HardenParent
        or RuleKind.HardenUpcoming or RuleKind.HardenNextUp or RuleKind.HardenEpisodes or RuleKind.HardenPlaylistItems or RuleKind.HardenPlaylist
        or RuleKind.HardenAggregates or RuleKind.HardenPeople or RuleKind.HardenSessionReport or RuleKind.HardenSessions or RuleKind.HardenItem
        or RuleKind.HardenLibrarySeries or RuleKind.HardenPlayCommand or RuleKind.GenericBackstop;

    /// <summary>
    /// Gets whether any of an action's hardening rules applies to a caller under the settings: not one for anonymous callers only
    /// (unless it covers them), not the library hardening while it is <c>Off</c>, never the <c>Strict</c> kinds (not done yet) or
    /// <see cref="RuleKind.BackstopExempt"/>. The filter neither runs nor times the rules otherwise.
    /// </summary>
    /// <param name="rules">The action's rules.</param>
    /// <param name="userId">The caller, or the empty GUID for an anonymous one.</param>
    /// <param name="settings">The hardening's settings.</param>
    /// <returns><c>true</c> when a rule applies.</returns>
    public static bool Applies(ActionRules rules, Guid userId, HardeningSettings settings)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(settings);
        return rules.Hardening.Any(r => Runs(r.Kind, userId, settings));
    }

    /// <summary>
    /// Gets the name the filter times an action's hardening rules under, and counts a rule that failed under: the name of the one
    /// that applies to the caller (<see cref="RuleName"/>), or, for an action with several, their names in the rule table's
    /// order joined with <c>+</c> (never only the first rule's). Without settings (they couldn't be read),
    /// every hardening rule of the action is named.
    /// </summary>
    /// <param name="rules">The action's rules.</param>
    /// <param name="userId">The caller, or the empty GUID for an anonymous one.</param>
    /// <param name="settings">The hardening's settings, or <c>null</c> when they couldn't be read.</param>
    /// <returns>The name.</returns>
    public static string RuleNames(ActionRules rules, Guid userId, HardeningSettings? settings)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var applying = rules.Hardening.Where(r => settings is null || Runs(r.Kind, userId, settings)).ToList();
        var named = applying.Count > 0 ? applying : rules.Hardening;
        return string.Join('+', named.Select(RuleName).Distinct(StringComparer.Ordinal));
    }

    // Whether one rule runs for a caller under the settings (Applies, RuleNames).
    private static bool Runs(RuleKind kind, Guid userId, HardeningSettings settings)
        => kind is not (RuleKind.BackstopExempt or RuleKind.StrictStream)
            && (userId != Guid.Empty || CoversAnonymousCallers(kind))
            && (settings.Library || !IsLibraryHardening(kind));

    /// <summary>
    /// Reads the hardening's settings, each with its default for a value this build doesn't know.
    /// </summary>
    /// <param name="configuration">The plugin's settings.</param>
    /// <returns>The settings.</returns>
    public static HardeningSettings Settings(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new HardeningSettings(
            Library: !string.Equals(configuration.LibraryHardening?.Trim(), "Off", StringComparison.OrdinalIgnoreCase),
            Redact: string.Equals(configuration.NonAdminConfigurationReads?.Trim(), "Redact", StringComparison.OrdinalIgnoreCase),
            AllowList: (configuration.ConfigurationReadAllowList ?? []).Where(k => !string.IsNullOrWhiteSpace(k)).Select(ConfigurationReadGuard.Normalize).ToHashSet(StringComparer.Ordinal),
            BlockRestart: configuration.BlockNonAdminRestart,
            TrimPublicUsers: configuration.TrimPublicUsers);
    }

    /// <summary>
    /// Applies an action's hardening rules before the action, for a caller that is neither an administrator nor an API key.
    /// </summary>
    /// <param name="context">The request; <c>Result</c> is set when the request is answered or refused here.</param>
    /// <param name="rules">The action's rules.</param>
    /// <param name="userId">The caller, or the empty GUID for an anonymous one.</param>
    /// <param name="locks">The caller's locks (read only when a lock decides).</param>
    /// <param name="settings">The hardening's settings.</param>
    /// <returns>What to do with the action's result, or <c>null</c>; it throws when it can't decide (the caller fails closed).</returns>
    public Action<ActionExecutedContext>? Before(ActionExecutingContext context, ActionRules rules, Guid userId, Func<IReadOnlySet<AccessRolesLock>> locks, HardeningSettings settings)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(locks);
        ArgumentNullException.ThrowIfNull(settings);
        var request = new Request(this, context, rules, userId);
        Action<ActionExecutedContext>? after = null;
        foreach (var rule in rules.Hardening)
        {
            if (request.Anonymous && !CoversAnonymousCallers(rule.Kind))
            {
                continue;
            }

            if (IsLibraryHardening(rule.Kind) && !settings.Library)
            {
                continue;
            }

            var step = Apply(request, rule, locks, settings);
            if (context.Result is not null)
            {
                return null;
            }

            after = step is null ? after : after is null ? step : after + step;
        }

        return after;
    }

    private static Guid AsGuid(object? value) => value switch
    {
        Guid id => id,
        string text when Guid.TryParse(text, out var parsed) => parsed,
        _ => Guid.Empty
    };

    private static Guid Argument(ActionExecutingContext context, string name)
        => context.ActionArguments.TryGetValue(name, out var value) ? AsGuid(value) : Guid.Empty;

    // An action's own empty answer, by the action, as its result type is.
    private static IActionResult EmptyFor(string action) => action switch
    {
        "GetQueryFiltersLegacy" => new OkObjectResult(new QueryFiltersLegacy()),
        "GetQueryFilters" => new OkObjectResult(new QueryFilters()),
        "GetSearchHints" => new OkObjectResult(new SearchHintResult([], 0)),
        "GetLatestMedia" or "GetLatestMediaLegacy" => new OkObjectResult(Array.Empty<BaseItemDto>()),
        _ => new OkObjectResult(new QueryResult<BaseItemDto>())
    };

    // A result with another value, everything else of the action's own kept (status, declared type, content types,
    // formatters), so the response is written as the action's would be.
    private static ObjectResult Replace(ObjectResult original, object value)
    {
        var replaced = new ObjectResult(value) { StatusCode = original.StatusCode, DeclaredType = original.DeclaredType };
        foreach (var type in original.ContentTypes)
        {
            replaced.ContentTypes.Add(type);
        }

        foreach (var formatter in original.Formatters)
        {
            replaced.Formatters.Add(formatter);
        }

        return replaced;
    }

    private Action<ActionExecutedContext>? Apply(Request request, ActionManifestRule rule, Func<IReadOnlySet<AccessRolesLock>> locks, HardeningSettings settings)
    {
        var context = request.Context;
        switch (rule.Kind)
        {
            case RuleKind.LockedReject when rule.Lock is AccessRolesLock.AdminSurfaces or AccessRolesLock.UserDirectory:
                if (locks().Contains(rule.Lock.Value))
                {
                    request.Refuse(rule, AdminSurfacesMessage);
                }

                return null;
            case RuleKind.SystemInfoBlank:
                // Every signed-in non-admin's under LibraryHardening Standard (the paths are no user's
                // business); with it Off, only under AdminSurfaces, as before.
                return settings.Library || locks().Contains(AccessRolesLock.AdminSurfaces) ? executed => BlankSystemInfo(executed) : null;
            case RuleKind.UserDirectory:
                return locks().Contains(AccessRolesLock.UserDirectory) ? UserDirectory(request, rule) : null;
            case RuleKind.ConfigurationReadGuard:
                return ConfigurationRead(request, rule, settings);
            case RuleKind.RestartBlock:
                if (settings.BlockRestart)
                {
                    request.Refuse(rule, RestartMessage);
                }

                return null;
            case RuleKind.PublicUsersTrim:
                return settings.TrimPublicUsers ? executed => TrimPublicUsers(executed) : null;
            case RuleKind.HardenLatest or RuleKind.HardenUpcoming or RuleKind.HardenAggregates:
                request.HideWhenHidden(rule, () => EmptyFor(request.Action), "parentId");
                return null;
            case RuleKind.HardenNextUp:
                request.HideWhenHidden(rule, () => EmptyFor(request.Action), "seriesId", "parentId");
                return null;
            case RuleKind.HardenParent:
                request.HideWhenHidden(rule, () => new NotFoundResult(), "parentId");
                return null;
            case RuleKind.HardenEpisodes:
                request.HideWhenHidden(rule, () => new NotFoundResult(), "seriesId", "seasonId");
                return null;
            case RuleKind.HardenItem:
                request.HideWhenHidden(rule, () => new NotFoundResult(), "itemId", "mediaSourceId");
                return null;
            case RuleKind.GenericBackstop:
                request.HideWhenHidden(rule, () => new NotFoundResult(), [.. request.Rules.ScopeParameters]);
                return null;
            case RuleKind.HardenItems:
                if (!request.HideWhenHidden(rule, () => new NotFoundResult(), "parentId"))
                {
                    // An emptied ids list is no filter at all (ItemsController.cs:369): without Recursive the action would
                    // answer the caller's root views, with it everything in the caller's scope. So it never runs with one.
                    request.FilterIds(rule, "ids", () => new OkObjectResult(new QueryResult<BaseItemDto>()));
                }

                return null;
            case RuleKind.HardenPlayCommand:
                request.FilterIds(rule, "itemIds", () => new NoContentResult());
                return null;
            case RuleKind.HardenPlaylistItems:
                // The caller's own id, whatever userId says: 10.11.5 lists another user's private playlist for it.
                context.ActionArguments[UserIdArgument] = (Guid?)request.UserId;
                return executed => DropHiddenItems(request, rule, executed);
            case RuleKind.HardenPlaylist:
                return executed => FilterPlaylist(request, rule, executed);
            case RuleKind.HardenPeople:
                if (request.HideWhenHidden(rule, () => new OkObjectResult(new QueryResult<BaseItemDto>()), "appearsInItemId"))
                {
                    return null;
                }

                return context.ActionArguments.TryGetValue("searchTerm", out var term) && term is string text && !string.IsNullOrWhiteSpace(text)
                    ? executed => FilterPeople(request, rule, executed)
                    : null;
            case RuleKind.HardenSessionReport:
                SessionReport(request, rule);
                return null;
            case RuleKind.HardenSessions:
                return executed => FilterSessions(request, rule, executed);
            case RuleKind.HardenLibrarySeries:
                var series = Argument(context, "librarySeriesId");
                if (series == Guid.Empty && context.ActionArguments.TryGetValue("body", out var body) && body is not null)
                {
                    series = AsGuid(body.GetType().GetProperty("LibrarySeriesId")?.GetValue(body));
                }

                if (request.Hidden(series))
                {
                    request.Hide(rule, EmptyFor(request.Action));
                }

                return null;
            default:
                // BackstopExempt; the Strict kinds (Access Roles 1.1).
                return null;
        }
    }

    // UserDirectory: another user's GET /Users/{id} is the core's own "not found" without the action; GET /Users
    // keeps only the caller.
    private Action<ActionExecutedContext>? UserDirectory(Request request, ActionManifestRule rule)
    {
        if (request.Context.ActionArguments.ContainsKey(UserIdArgument) || string.Equals(request.Action, "GetUserById", StringComparison.Ordinal))
        {
            var target = Argument(request.Context, UserIdArgument);
            if (target != Guid.Empty && target != request.UserId)
            {
                request.Hide(rule, new NotFoundObjectResult(UserNotFound));
            }

            return null;
        }

        return executed =>
        {
            if (executed.Result is ObjectResult { Value: IEnumerable<UserDto> users } result)
            {
                var all = users.ToList();
                var own = all.Where(u => u.Id == request.UserId).ToList();
                executed.Result = Replace(result, own);
                if (own.Count != all.Count)
                {
                    request.Count(rule, BlockCounter.Hidden);
                }
            }
        };
    }

    // Configuration reads: AllowList refuses GET /System/Configuration and every key not on the list (the key
    // normalised); Redact lets every read on. Either way the answer is a copy with the type's secrets (and the encoding
    // options' server paths) stripped; in Redact mode a type the guard doesn't know is refused, in AllowList mode an
    // allow-listed one goes as it is (nothing known to strip, and the live object is never touched).
    private Action<ActionExecutedContext>? ConfigurationRead(Request request, ActionManifestRule rule, HardeningSettings settings)
    {
        if (!settings.Redact)
        {
            var named = string.Equals(request.Action, "GetNamedConfiguration", StringComparison.Ordinal);
            var key = request.Context.ActionArguments.TryGetValue("key", out var value) ? value as string : null;
            if (!named || key is null || !settings.AllowList.Contains(ConfigurationReadGuard.Normalize(key)))
            {
                request.Refuse(rule, ConfigurationMessage);
                return null;
            }
        }

        return executed =>
        {
            if (executed.Result is not ObjectResult { Value: { } live } result)
            {
                return;
            }

            if (!ConfigurationReadGuard.IsKnown(live.GetType()))
            {
                if (settings.Redact)
                {
                    executed.Result = KitProblemDetails.Result(PluginErrorKind.Locked, ConfigurationMessage);
                    request.Count(rule, BlockCounter.Refused);
                }

                return;
            }

            executed.Result = Replace(result, _configuration.Copy(live, out _));
        };
    }

    // GET /System/Info for every signed-in non-admin under LibraryHardening Standard, and under AdminSurfaces whatever it is
    // (since 0.9.9): the obsolete server-path fields blanked on a shallow copy of the per-call DTO
    // (SystemInfo.cs:79-122).
    private static void BlankSystemInfo(ActionExecutedContext executed)
    {
        if (executed.Result is not ObjectResult { Value: SystemInfo info } result)
        {
            return;
        }

        var copy = (SystemInfo)_memberwiseClone.Invoke(info, null)!;
#pragma warning disable CS0618 // The obsolete fields are what the rule blanks.
        copy.ProgramDataPath = string.Empty;
        copy.WebPath = string.Empty;
        copy.ItemsByNamePath = string.Empty;
        copy.CachePath = string.Empty;
        copy.LogPath = string.Empty;
        copy.InternalMetadataPath = string.Empty;
        copy.TranscodingTempPath = string.Empty;
#pragma warning restore CS0618
        executed.Result = Replace(result, copy);
    }

    // GET /Users/Public: each user trimmed to the login screen's fields, as new DTOs.
    private static void TrimPublicUsers(ActionExecutedContext executed)
    {
        if (executed.Result is not ObjectResult { Value: IEnumerable<UserDto> users } result)
        {
            return;
        }

        executed.Result = Replace(result, users.Select(u => new UserDto
        {
            Id = u.Id,
            Name = u.Name,
            ServerId = u.ServerId,
            PrimaryImageTag = u.PrimaryImageTag,
            HasPassword = u.HasPassword,
            HasConfiguredPassword = u.HasConfiguredPassword,
#pragma warning disable CS0618 // One of the login screen's fields, obsolete since 10.9 but still sent.
            HasConfiguredEasyPassword = u.HasConfiguredEasyPassword,
#pragma warning restore CS0618
            Configuration = null!,
            Policy = null!
        }).ToList());
    }

    // GET /Playlists/{id}/Items: the members the caller can't see dropped from the page, the total less them.
    private static void DropHiddenItems(Request request, ActionManifestRule rule, ActionExecutedContext executed)
    {
        if (executed.Result is not ObjectResult { Value: QueryResult<BaseItemDto> page } result)
        {
            return;
        }

        var kept = page.Items.Where(dto => dto is null || !request.Hidden(dto.Id)).ToArray();
        if (kept.Length != page.Items.Count)
        {
            executed.Result = Replace(result, new QueryResult<BaseItemDto>(page.StartIndex, Math.Max(0, page.TotalRecordCount - (page.Items.Count - kept.Length)), kept));
            request.Count(rule, BlockCounter.Hidden);
        }
    }

    // GET /Playlists/{id} (it leaks on 10.11.5): the DTO's ItemIds without the members the caller can't see, on a new DTO.
    private static void FilterPlaylist(Request request, ActionManifestRule rule, ActionExecutedContext executed)
    {
        if (executed.Result is not ObjectResult { Value: PlaylistDto playlist } result)
        {
            return;
        }

        var kept = playlist.ItemIds.Where(id => !request.Hidden(id)).ToList();
        if (kept.Count != playlist.ItemIds.Count)
        {
            executed.Result = Replace(result, new PlaylistDto { OpenAccess = playlist.OpenAccess, Shares = playlist.Shares, ItemIds = kept });
            request.Count(rule, BlockCounter.Hidden);
        }
    }

    // GET /Persons?searchTerm=: the names without an item in the caller's libraries dropped, decided by
    // one database query for the page.
    private void FilterPeople(Request request, ActionManifestRule rule, ActionExecutedContext executed)
    {
        if (executed.Result is not ObjectResult { Value: QueryResult<BaseItemDto> page } result || page.Items.Count == 0)
        {
            return;
        }

        var people = _people ?? throw new InvalidOperationException("The server's database isn't available to Access Roles' People-search rule.");
        var names = page.Items.Select(p => p?.Name).OfType<string>().Where(n => n.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        var libraries = LibraryAccess.TopParentIds(_access.VisibleLibraries(request.User));
        var visible = names.Count == 0 || libraries.Length == 0 ? new HashSet<string>(StringComparer.Ordinal) : people.NamesWithItemsIn(names, libraries);
        var kept = page.Items.Where(p => p?.Name is { } name && visible.Contains(name)).ToArray();
        if (kept.Length != page.Items.Count)
        {
            executed.Result = Replace(result, new QueryResult<BaseItemDto>(kept));
            request.Count(rule, BlockCounter.Hidden);
        }
    }

    // GET /Sessions: what the sessions play or view that the caller can't see, taken out of the per-call
    // DTOs; the item DTOs and lists they point to are the live sessions' and are replaced, never changed.
    private static void FilterSessions(Request request, ActionManifestRule rule, ActionExecutedContext executed)
    {
        if (executed.Result is not ObjectResult { Value: IEnumerable<SessionInfoDto> sessions } result)
        {
            return;
        }

        var list = sessions.ToList();
        var hid = false;
        foreach (var session in list.OfType<SessionInfoDto>())
        {
            if (session.NowPlayingItem is { } playing && request.Hidden(playing.Id))
            {
                session.NowPlayingItem = null;
                hid = true;
            }

            if (session.NowViewingItem is { } viewing && request.Hidden(viewing.Id))
            {
                session.NowViewingItem = null;
                hid = true;
            }

            if (session.NowPlayingQueue is { Count: > 0 } queue && queue.Any(q => q is not null && request.Hidden(q.Id)))
            {
                session.NowPlayingQueue = [.. queue.Where(q => q is null || !request.Hidden(q.Id))];
                hid = true;
            }

            if (session.NowPlayingQueueFullItems is { Count: > 0 } full && full.Any(d => d is not null && request.Hidden(d.Id)))
            {
                session.NowPlayingQueueFullItems = [.. full.Where(d => d is null || !request.Hidden(d.Id))];
                hid = true;
            }
        }

        executed.Result = Replace(result, list);
        if (hid)
        {
            request.Count(rule, BlockCounter.Hidden);
        }
    }

    // The playback and viewing reports: a report about an item the caller can't see is answered 204, the
    // actions' own answer, without running; the body's queue keeps the entries the caller can see.
    private static void SessionReport(Request request, ActionManifestRule rule)
    {
        var context = request.Context;
        var item = Argument(context, "itemId");
        QueueItem[]? queue = null;
        Action<QueueItem[]>? setQueue = null;
        foreach (var argument in context.ActionArguments.Values)
        {
            switch (argument)
            {
                case PlaybackProgressInfo progress:
                    item = progress.ItemId;
                    queue = progress.NowPlayingQueue;
                    setQueue = q => progress.NowPlayingQueue = q;
                    break;
                case PlaybackStopInfo stop:
                    item = stop.ItemId;
                    queue = stop.NowPlayingQueue;
                    setQueue = q => stop.NowPlayingQueue = q;
                    break;
            }
        }

        if (request.Hidden(item))
        {
            request.Hide(rule, new NoContentResult());
            return;
        }

        if (queue is { Length: > 0 } && setQueue is not null && queue.Any(q => q is not null && request.Hidden(q.Id)))
        {
            setQueue([.. queue.Where(q => q is null || !request.Hidden(q.Id))]);
            request.Count(rule, BlockCounter.Hidden);
        }
    }

    /// <summary>One request's caller, its lookups and its answers.</summary>
    private sealed class Request
    {
        private readonly ServerHardening _owner;
        private readonly Dictionary<Guid, ItemVisibility> _visibility = [];
        private User? _user;

        public Request(ServerHardening owner, ActionExecutingContext context, ActionRules rules, Guid userId)
        {
            _owner = owner;
            Context = context;
            Rules = rules;
            UserId = userId;
            Action = (context.ActionDescriptor as ControllerActionDescriptor)?.ActionName ?? string.Empty;
        }

        public ActionExecutingContext Context { get; }

        public ActionRules Rules { get; }

        public Guid UserId { get; }

        public string Action { get; }

        public bool Anonymous => UserId == Guid.Empty;

        // The caller's user; a signed-in caller the user manager doesn't know can't be checked (the filter fails closed).
        public User User => _user ??= _owner._userManager.GetUserById(UserId) ?? throw new InvalidOperationException("The caller's user wasn't found.");

        public bool Hidden(Guid id)
        {
            if (id == Guid.Empty)
            {
                return false;
            }

            if (!_visibility.TryGetValue(id, out var visibility))
            {
                visibility = _owner._access.Visibility(id, User);
                _visibility[id] = visibility;
            }

            return visibility == ItemVisibility.Hidden;
        }

        public void Count(ActionManifestRule rule, string outcome)
            => _owner._counter.Record(Anonymous ? null : UserId, RuleName(rule), Rules.Endpoint, outcome);

        public void Refuse(ActionManifestRule rule, string detail)
        {
            Context.Result = KitProblemDetails.Result(PluginErrorKind.Locked, detail);
            Count(rule, BlockCounter.Refused);
        }

        public void Hide(ActionManifestRule rule, IActionResult answer)
        {
            Context.Result = answer;
            Count(rule, BlockCounter.Hidden);
        }

        // Answers when one of the arguments names an item the caller can't see; returns whether it did.
        public bool HideWhenHidden(ActionManifestRule rule, Func<IActionResult> answer, params string[] arguments)
        {
            foreach (var name in arguments)
            {
                if (Hidden(Argument(Context, name)))
                {
                    Hide(rule, answer());
                    return true;
                }
            }

            return false;
        }

        // An id list without the ids the caller can't see; with none left, the answer instead of the action.
        public void FilterIds(ActionManifestRule rule, string argument, Func<IActionResult> whenNoneLeft)
        {
            if (!Context.ActionArguments.TryGetValue(argument, out var value) || value is not Guid[] { Length: > 0 } ids)
            {
                return;
            }

            var kept = ids.Where(id => !Hidden(id)).ToArray();
            if (kept.Length == ids.Length)
            {
                return;
            }

            if (kept.Length == 0)
            {
                Hide(rule, whenNoneLeft());
                return;
            }

            Context.ActionArguments[argument] = kept;
            Count(rule, BlockCounter.Hidden);
        }
    }
}
