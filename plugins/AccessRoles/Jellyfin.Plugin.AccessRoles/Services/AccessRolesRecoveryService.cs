using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AccessRoles.Access;
using Jellyfin.Plugin.AccessRoles.Api.Models;
using Jellyfin.Plugin.AccessRoles.Audit;
using Jellyfin.Plugin.AccessRoles.Enforcement;
using Jellyfin.Plugin.AccessRoles.Model;
using Jellyfin.Plugin.AccessRoles.Policy;
using Jellyfin.Plugin.AccessRoles.Storage;
using Jellyfin.Plugin.AccessRoles.Sync;
using Jellyfin.Plugin.Kit.Api;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AccessRoles.Services;

/// <summary>
/// Lockout and recovery, behind <c>POST Degraded/Resolve</c> and <c>POST ReleaseLocks</c>:
/// the plugin's usual write pattern, a dry run whose <c>PreviewToken</c> the confirmation sends back as <c>ifPreview</c>, a 409 <c>Changed</c> that
/// names only the users that changed since, and an audit line.
/// </summary>
internal sealed class AccessRolesRecoveryService : IAccessRolesRecovery
{
    /// <summary>The 409 code of a call with nothing to do: not degraded.</summary>
    public const string NotDegraded = "NotDegraded";

    /// <summary>The 409 code of Degraded mode a read-only store holds, which an admin can't end from the page.</summary>
    public const string StoreReadOnly = "StoreReadOnly";

    /// <summary>The audit reason of the mirror's flags put back by <c>ReleaseLocks</c>.</summary>
    public const string ReleaseLocksReason = "ReleaseLocks";

    private readonly AccessRolesStore _store;
    private readonly IServerUsers _users;
    private readonly AccessRolesAuditLog _audit;
    private readonly ILogger _logger;
    private readonly ILibraryCatalogSource? _catalog;
    private readonly PolicyWriter? _writer;
    private readonly PreferenceMirrorSync? _mirror;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="AccessRolesRecoveryService"/> class.
    /// </summary>
    /// <param name="store">The store.</param>
    /// <param name="users">The server's users.</param>
    /// <param name="audit">The audit log.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="catalog">The library catalog (<c>ReleaseLocks</c>' original access).</param>
    /// <param name="writer">The only policy writer (<c>ReleaseLocks</c>).</param>
    /// <param name="mirror">The optional native mirror (<c>ReleaseLocks</c>).</param>
    /// <param name="timeProvider">The clock; the system clock by default.</param>
    public AccessRolesRecoveryService(AccessRolesStore store, IServerUsers users, AccessRolesAuditLog audit, ILogger logger, ILibraryCatalogSource? catalog = null, PolicyWriter? writer = null, PreferenceMirrorSync? mirror = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _users = users;
        _audit = audit;
        _logger = logger;
        _catalog = catalog;
        _writer = writer;
        _mirror = mirror;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<AccessRolesDegradedDto> ResolveDegradedAsync(AccessRolesWriteOptions options, ClaimsPrincipal caller, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var degraded = _store.Degraded;
        var state = degraded.Current ?? throw new AccessRolesException(PluginErrorKind.Conflict, NotDegraded, "Access Roles isn't in Degraded mode: nothing was changed.");
        if (!degraded.CanResolve)
        {
            throw new AccessRolesException(
                PluginErrorKind.Conflict,
                StoreReadOnly,
                "Access Roles' data file is read-only (" + state.Because + "), so Degraded mode can't be ended here: put a readable copy back, or install the plugin version that wrote it, with the server stopped. Nothing was changed.");
        }

        var lines = Lines(state);
        var token = AccessRolesTokens.Of(lines);
        var answer = Describe(state, options.DryRun, token);
        if (options.DryRun)
        {
            return answer;
        }

        if (!AccessRolesTokens.Same(token, options.IfPreview))
        {
            throw Changed(options.IfPreview, lines, "Degraded mode doesn't lock the users its preview showed any more");
        }

        DegradedState? ended;
        try
        {
            ended = degraded.Resolve();
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            // A marker that can't be deleted is a 503 with a code, not a 500; nothing changed.
            _logger.LogError(ex, "Access Roles couldn't delete its Degraded mode marker {File}", degraded.MarkerPath);
            throw new AccessRolesException(
                PluginErrorKind.Unavailable,
                DegradedMode.MarkerNotDeleted,
                "Access Roles couldn't delete its Degraded mode marker (" + DegradedMode.MarkerFileName + " in its data folder), so the users stay locked. Nothing was changed. Try again; the server log has the details.");
        }

        if (ended is null)
        {
            throw new AccessRolesException(PluginErrorKind.Conflict, NotDegraded, "Access Roles isn't in Degraded mode any more: nothing was changed.");
        }

        var actor = AuditActor.Of(caller, id => _users.Find(id)?.Name);
        await _audit.AppendAsync([DegradedAudit.Ended(actor, ended.Users, "AdminConfirmed", ended.EveryNonAdmin)], cancellationToken).ConfigureAwait(false);
        _logger.LogWarning(
            "An admin ended Access Roles' Degraded mode ({Because}): {Who} get only what the data file gives them",
            ended.Because,
            ended.EveryNonAdmin ? "every non-admin" : ended.Users.Count.ToString(CultureInfo.InvariantCulture) + " users");
        answer.Resolved = true;
        answer.DryRun = false;
        return answer;
    }

    /// <inheritdoc />
    public async Task<AccessRolesReleaseLocksDto> ReleaseLocksAsync(AccessRolesReleaseLocksRequest? body, bool restoreLibraries, AccessRolesWriteOptions options, ClaimsPrincipal caller, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (_catalog is null || _writer is null || _mirror is null)
        {
            throw new InvalidOperationException("ReleaseLocks needs the catalog, the policy writer and the mirror.");
        }

        // A read-only store releases too: the flags and library access are the users' own
        // policies; only leaving management (Excluded) needs the data file, so nobody is excluded.
        var readOnly = _store.IsReadOnly;
        var document = _store.Current;
        var evidence = _store.Degraded.MirrorEvidence;
        var catalog = _catalog.Read();
        var users = _users.GetAll();
        var asked = body?.UserIds is { Count: > 0 } ids ? ids.Distinct().ToList() : null;
        if (asked?.Where(id => !id.Equals(Guid.Empty) && !users.Any(u => u.Id.Equals(id))).Select(id => (Guid?)id).FirstOrDefault() is { } unknown)
        {
            throw new AccessRolesException(PluginErrorKind.NotFound, "UnknownUser", "There is no user " + AccessRolesTokens.Key(unknown) + ". Nothing was changed.");
        }

        // Everyone with a native lock to release: a flag the mirror turned off (recorded in the document, or in a copy of it
        // the store couldn't use: Degraded mode's evidence), and with restoreLibraries a ledger entry.
        var candidates = users
            .Where(u => asked is null
                ? Recorded(document, evidence, readOnly, u) || (restoreLibraries && document.Ledger.ContainsKey(u.Id))
                : asked.Contains(u.Id))
            .OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var plans = candidates.Select(u => PlanRelease(document, evidence, readOnly, catalog, u, restoreLibraries)).ToList();
        var planned = plans.Where(p => p.Target is not null).ToDictionary(p => p.User.Id, p => p.Target!);
        var refused = LockoutGuard.Refused(catalog, users, planned, _ => options.ConfirmNoFullAdmin);
        plans = [.. plans.Select(p => refused.Contains(p.User.Id) ? p with { Target = null, Skipped = AccessRolesException.NoFullAdministrator } : p)];

        // Everyone: only the users it does something for, or would but for the guard (a user released before, Excluded with
        // the original access written, isn't listed again).
        if (asked is null)
        {
            plans = [.. plans.Where(p => p.Mirrored || p.Target is not null || p.Exclude || p.Skipped == AccessRolesException.NoFullAdministrator)];
        }

        var lines = plans.ToDictionary(p => AccessRolesTokens.Key(p.User.Id), Line, StringComparer.Ordinal);
        lines["restoreLibraries"] = restoreLibraries ? "true" : "false";
        var token = AccessRolesTokens.Of(lines);
        var answer = new AccessRolesReleaseLocksDto { DryRun = options.DryRun, PreviewToken = options.DryRun ? token : null, RestoreLibraries = restoreLibraries, StoreReadOnly = readOnly, Released = [.. plans.Select(Describe)] };
        if (options.DryRun)
        {
            return answer;
        }

        if (!AccessRolesTokens.Same(token, options.IfPreview))
        {
            throw Changed(options.IfPreview, lines, "The locks to release aren't the ones the preview showed any more");
        }

        var actor = AuditActor.Of(caller, id => _users.Find(id)?.Name);

        // First out of management, so neither the library sync nor the mirror writes the locks again meanwhile.
        var excluding = plans.Where(p => p.Exclude).Select(p => p.User.Id).ToHashSet();
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        if (excluding.Count > 0)
        {
            await _store.UpdateAsync(
                d => d with { Assignments = [.. d.Assignments.Select(a => excluding.Contains(a.UserId) && a.State.Is(AccessRolesAssignmentState.Managed) ? a with { State = AccessRolesAssignmentState.Excluded, UpdatedUtc = now } : a)] },
                cancellationToken).ConfigureAwait(false);
        }

        var flags = await _mirror.ReleaseAsync(actor, [.. plans.Where(p => p.Mirrored).Select(p => p.User.Id)], ReleaseLocksReason, CancellationToken.None).ConfigureAwait(false);
        var released = new List<AccessRolesReleasedUserDto>();
        foreach (var plan in plans)
        {
            var row = Describe(plan);
            // No result of the release for a recorded flag: a run of the mirror between the plan and here put it back on
            // (the user left management first) or forgot it; the flag as it is now tells which.
            row.PreferenceOutcome = plan.Mirrored
                ? flags.FirstOrDefault(f => f.UserId.Equals(plan.User.Id))?.Action.ToString()
                    ?? (_users.Find(plan.User.Id)?.Native.EnableUserPreferenceAccess == true ? nameof(MirrorAction.TurnedOn) : nameof(MirrorAction.Forgotten))
                : null;
            if (plan.Target is { } target)
            {
                var write = await _writer.WriteAsync(plan.User.Id, target, CancellationToken.None, expected: plan.User.Native.Libraries).ConfigureAwait(false);
                row.LibrariesOutcome = write.Outcome.ToString();
            }

            released.Add(row);
        }

        await _audit.AppendAsync(
            [new AuditRecord(actor, AuditActions.LocksReleased, null, null, null, new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["RestoreLibraries"] = restoreLibraries,
                ["Everyone"] = asked is null,
                ["Users"] = released.Select(r => new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["UserId"] = AccessRolesAuditDetails.Id(r.UserId),
                    ["Name"] = r.Name,
                    ["PreferenceAccess"] = r.PreferenceOutcome,
                    ["Libraries"] = r.LibrariesOutcome ?? r.LibrariesSkipped,
                    ["Excluded"] = r.Excluded
                }).ToList()
            })],
            CancellationToken.None).ConfigureAwait(false);
        _logger.LogWarning("An admin released Access Roles' native locks of {Count} users (original library access restored: {Restore})", released.Count, restoreLibraries);
        answer.Released = released;
        answer.DryRun = false;
        return answer;
    }

    // Whether the mirror recorded turning the user's flag off: in the document, or in Degraded mode's evidence while the flag
    // is still off; on a read-only store, whose records can't be dropped, only while the flag is off.
    private static bool Recorded(AccessRolesDocument document, IReadOnlySet<Guid> evidence, bool readOnly, ServerUser user)
        => (document.PreferenceMirror.ContainsKey(user.Id) && (!readOnly || !user.Native.EnableUserPreferenceAccess))
            || (evidence.Contains(user.Id) && !user.Native.EnableUserPreferenceAccess);

    // One user's release: the mirror's flag, the original access (filtered as onUnmanage=Restore filters it), and whether
    // they leave management (never on a read-only store).
    private static ReleasePlan PlanRelease(AccessRolesDocument document, IReadOnlySet<Guid> evidence, bool readOnly, LibraryCatalog catalog, ServerUser user, bool restoreLibraries)
    {
        var mirrored = Recorded(document, evidence, readOnly, user);
        LibraryFields? target = null;
        string? skipped = null;
        IReadOnlyList<Guid> recreated = [];
        if (restoreLibraries)
        {
            if (!document.Ledger.TryGetValue(user.Id, out var entry))
            {
                skipped = AccessRolesException.NoLedgerEntry;
            }
            else
            {
                var original = entry.OriginalEnableAllFolders ? LibraryFields.All : LibraryFields.Of(false, EffectiveRulesResolver.OriginalLibraries(document, catalog, entry, out recreated));
                if (original.Same(user.Native.Libraries))
                {
                    skipped = "AlreadyApplied";
                }
                else
                {
                    target = original;
                }
            }
        }

        var something = mirrored || target is not null || (restoreLibraries && document.Ledger.ContainsKey(user.Id));
        var exclude = something && !readOnly && EffectiveRulesResolver.FindAssignment(document, user.Id) is { } assignment && assignment.State.Is(AccessRolesAssignmentState.Managed);
        return new ReleasePlan(user, mirrored, target, skipped, recreated, exclude);
    }

    private static string Line(ReleasePlan plan)
        => string.Join(
            "|",
            plan.Mirrored ? "mirrored" : "-",
            plan.User.Native.EnableUserPreferenceAccess ? "on" : "off",
            plan.User.Native.Libraries.ToString(),
            plan.Target?.ToString() ?? plan.Skipped ?? "-",
            plan.Exclude ? "exclude" : "-");

    private AccessRolesReleasedUserDto Describe(ReleasePlan plan) => new()
    {
        UserId = plan.User.Id,
        Name = plan.User.Name,
        PreferenceAccess = plan.Mirrored && !plan.User.Native.EnableUserPreferenceAccess,
        Libraries = plan.Target is { } target ? new AccessRolesNativeLibrariesDto { EnableAllFolders = target.EnableAllFolders, EnabledFolders = [.. target.EnabledFolders] } : null,
        RecreatedLeftOut = plan.Recreated,
        LibrariesSkipped = plan.Skipped,
        Excluded = plan.Exclude
    };

    // The token's lines: one per user locked, and whether every non-admin is.
    private static Dictionary<string, string> Lines(DegradedState state)
    {
        var lines = state.Users.ToDictionary(AccessRolesTokens.Key, _ => "locked", StringComparer.Ordinal);
        lines["everyNonAdmin"] = state.EveryNonAdmin ? "true" : "false";
        lines["because"] = state.Because;
        return lines;
    }

    private AccessRolesDegradedDto Describe(DegradedState state, bool dryRun, string token) => new()
    {
        Because = state.Because,
        EveryNonAdmin = state.EveryNonAdmin,
        Users = [.. state.Users.Order().Select(Ref)],
        Source = state.Source,
        SinceUtc = state.SinceUtc,
        FromEarlierStart = state.FromEarlierStart,
        DryRun = dryRun,
        PreviewToken = dryRun ? token : null
    };

    private AccessRolesUserRefDto Ref(Guid id) => new() { UserId = id, Name = _users.Find(id)?.Name ?? string.Empty };

    // A 409 Changed naming only the users whose lines differ from the token's, or nobody when
    // the token's lines aren't remembered.
    private AccessRolesException Changed(string? given, IReadOnlyDictionary<string, string> lines, string what)
    {
        var differing = AccessRolesTokens.Differing(given, lines) ?? new HashSet<string>(StringComparer.Ordinal);
        var named = differing.Select(key => Guid.TryParse(key, out var id) ? id : (Guid?)null).OfType<Guid>().Select(Ref).ToList();
        return new AccessRolesException(
            PluginErrorKind.Conflict,
            AccessRolesException.Changed,
            what + (named.Count > 0 ? " (for " + string.Join(", ", named.Select(u => u.Name.Length > 0 ? u.Name : AccessRolesTokens.Key(u.UserId))) + ")" : string.Empty) + ". Nothing was changed. Preview it again.",
            named);
    }

    // What ReleaseLocks does for one user.
    private sealed record ReleasePlan(ServerUser User, bool Mirrored, LibraryFields? Target, string? Skipped, IReadOnlyList<Guid> Recreated, bool Exclude);
}
