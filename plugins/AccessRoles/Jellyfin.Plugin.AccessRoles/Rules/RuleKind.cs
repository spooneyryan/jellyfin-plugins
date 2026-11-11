namespace Jellyfin.Plugin.AccessRoles.Rules;

/// <summary>
/// What the global action filter does with one controller action (through the <c>ActionRuleTable</c>). The
/// <see cref="ActionManifest"/> marks every action of Jellyfin.Api with its kinds, or with none and a reviewed reason;
/// <c>AccessRulesActionFilter</c> applies them: the settings rules since 0.9.5, the admin surfaces, the server-wide rules and
/// the library leak hardening (<c>ServerHardening</c>) since 0.9.7; the <c>Strict</c> kinds are not applied yet.
/// </summary>
/// <remarks>
/// Rules apply to signed-in non-admin callers that are not API keys unless a kind says otherwise; administrators are never
/// restricted. The names are written into the embedded manifest, so renaming one is a manifest change.
/// </remarks>
internal enum RuleKind
{
    /// <summary>403 Locked when the rule's lock applies to the caller (password, user image, Quick Connect, admin surfaces).</summary>
    LockedReject,

    /// <summary>
    /// <c>UpdateUser</c>: refuse a name change (compared ordinally, as the core does) under the Profile lock; otherwise merge
    /// the stored values of locked fields into <c>updateUser.Configuration</c>.
    /// </summary>
    UserUpdateMerge,

    /// <summary><c>UpdateUserConfiguration</c>: merge the stored values of locked fields into the posted configuration.</summary>
    UserConfigurationMerge,

    /// <summary>
    /// <c>UpdateDisplayPreferences</c> for the <c>usersettings</c> document (its id derived as the action derives it): strip
    /// home sections and locked keys in any letter case, then add back the stored ones.
    /// </summary>
    DisplayPreferencesMerge,

    /// <summary>Server-wide: non-admin configuration reads only by the allow-list (or redacted, on a copy).</summary>
    ConfigurationReadGuard,

    /// <summary>Server-wide, anonymous callers included: restart only for the Administrator claim or an API key.</summary>
    RestartBlock,

    /// <summary>Server-wide, anonymous callers included: the public user list trimmed to the login fields.</summary>
    PublicUsersTrim,

    /// <summary>
    /// The obsolete server-path fields of <c>GET /System/Info</c> blanked on a copy: for every signed-in non-admin under
    /// <c>LibraryHardening</c> Standard, and under the <c>AdminSurfaces</c> lock whatever it is.
    /// </summary>
    SystemInfoBlank,

    /// <summary>The <c>UserDirectory</c> lock: <c>GET /Users</c> filtered to the caller, another user's <c>GET /Users/{id}</c> 404.</summary>
    UserDirectory,

    /// <summary>
    /// Admin or API-key callers, managed target: take the per-user lock and pre-merge the role result into the posted policy
    /// (fail open).
    /// </summary>
    UserPolicyPreMerge,

    /// <summary>Admin or API-key callers, managed target: take the per-user lock around the action (no change to the request).</summary>
    PerUserLock,

    /// <summary>Any caller: after a sign-in, correct a managed user's library fields before the response leaves.</summary>
    SignInHook,

    /// <summary>Admin or API-key callers: record the library the call affects, then reconcile at once after a 2xx result.</summary>
    LibraryStructureHook,

    /// <summary>Hardening, server-wide: Latest answers an empty list when <c>parentId</c> is invisible to the caller.</summary>
    HardenLatest,

    /// <summary>
    /// Hardening, server-wide: <c>ids</c> filtered to the visible ones (an empty result, the action not run, when none is
    /// left) and an invisible <c>parentId</c> 404.
    /// </summary>
    HardenItems,

    /// <summary>Hardening, server-wide: an invisible <c>parentId</c> (series, season, folder or view) answers 404.</summary>
    HardenParent,

    /// <summary>Hardening, server-wide: upcoming episodes empty when <c>parentId</c> is invisible.</summary>
    HardenUpcoming,

    /// <summary>Hardening, server-wide: Next Up empty when <c>seriesId</c> is invisible.</summary>
    HardenNextUp,

    /// <summary>Hardening, server-wide, 10.11 only: a series' episodes 404 when the series or season is invisible.</summary>
    HardenEpisodes,

    /// <summary>Hardening, server-wide: a playlist's items with the caller's id forced and invisible items dropped.</summary>
    HardenPlaylistItems,

    /// <summary>Hardening, server-wide: a playlist's <c>ItemIds</c> filtered to the visible items (it leaks on 10.11.5).</summary>
    HardenPlaylist,

    /// <summary>Hardening, server-wide: genre, studio, artist, year, filter and search aggregates empty when <c>parentId</c> is invisible.</summary>
    HardenAggregates,

    /// <summary>
    /// Hardening, server-wide: people search keeps only names with an item the caller can see, and <c>appearsInItemId</c> of
    /// an invisible item answers empty (both leak on 10.11.5).
    /// </summary>
    HardenPeople,

    /// <summary>
    /// Hardening, server-wide: a playback or viewing report about an item the caller can't see answers 204
    /// without running the action. 10.11.5 resolves a report's item without a user (<c>SessionManager.GetNowPlayingItem</c>,
    /// L720-737; <c>ReportNowViewingItem</c>, L1854-1862) and keeps its DTO, path included, as the session's
    /// <c>NowPlayingItem</c> or <c>NowViewingItem</c>, which <c>GET /Sessions</c> and the Sessions WebSocket feed return to
    /// the caller. The id is the route's <c>itemId</c>, <c>ReportViewing</c>'s query <c>itemId</c> (a string, parsed as the
    /// core parses it) or the body's <c>ItemId</c>; one that resolves without a user but not for the caller
    /// (<c>GetItemById&lt;BaseItem&gt;(id, user)</c>) is refused, one that doesn't resolve at all (or is empty) passes, as the
    /// core then finds nothing either. The body's <c>NowPlayingQueue</c> keeps only the entries the caller can see
    /// (<c>UpdateNowPlayingItem</c> builds <c>NowPlayingQueueFullItems</c> from it without a user, L457-467).
    /// </summary>
    /// <remarks>
    /// A report the filter drops leaves the session as it was: a stale <c>NowPlayingItem</c> of an item that became invisible
    /// while it played stays until the core's idle check stops the session (<c>CheckForIdlePlayback</c>, every 5 minutes,
    /// L644-680), and <see cref="HardenSessions"/> blanks it for its readers meanwhile.
    /// The bypass matrix's hardening probe (a sandbox probe, not included in this repository) runs the leak requests for it (the
    /// test project's <c>Rules/BypassMatrixLeakRowsTests</c> lists them).
    /// </remarks>
    HardenSessionReport,

    /// <summary>
    /// Hardening, server-wide: <c>GET /Sessions</c>' items of libraries the caller can't see are taken out of
    /// the result: <c>NowPlayingItem</c> and <c>NowViewingItem</c> set to <c>null</c>, <c>NowPlayingQueue</c> and
    /// <c>NowPlayingQueueFullItems</c> filtered. A non-admin with the native <c>EnableRemoteControlOfOtherUsers</c> gets
    /// other users' sessions (<c>?controllableByUserId=</c>, <c>SessionManager.GetSessions</c> L1974-2010), and those name
    /// what the others play. The result's <c>SessionInfoDto</c>s are made per call (<c>ToSessionInfoDto</c>, L1178-1212),
    /// so the rule sets their properties; the item DTOs and lists they point to are the live sessions' and are never changed.
    /// </summary>
    /// <remarks>The hardening probe runs it with a second, unrestricted user's session playing a hidden item.</remarks>
    HardenSessions,

    /// <summary>
    /// Hardening, server-wide: an action that reads an item without any visibility check answers 404 when the
    /// item ids it takes (the route's <c>itemId</c>, and <c>mediaSourceId</c> when given) resolve without a user but not for
    /// the caller.
    /// </summary>
    /// <remarks>
    /// The sandbox's fixture has no trickplay tiles, so the playlist is 404 natively there too; the unit tests run it.
    /// </remarks>
    HardenItem,

    /// <summary>
    /// Hardening, server-wide (added in 0.9.7): the Live TV guide filtered by a library series
    /// (<c>GetLiveTvPrograms</c>' <c>librarySeriesId</c>, <c>GetPrograms</c>' body <c>LibrarySeriesId</c>) answers an empty
    /// result when the series is invisible to the caller: 10.11.5 resolves it without a user and filters the guide by its
    /// name (<c>LiveTvController.cs</c> L626-635, L681-690).
    /// </summary>
    HardenLibrarySeries,

    /// <summary>
    /// Hardening, server-wide (added in 0.9.7): a remote-control play command
    /// (<c>SessionController.Play</c>) loses the <c>itemIds</c> the caller can't see; with none left it answers 204 without
    /// the action. 10.11.5's <c>TranslateItemForPlayback</c> resolves them without a user (<c>SessionManager.cs</c>
    /// L1382-1433), so a hidden folder's child ids could reach the target session.
    /// </summary>
    HardenPlayCommand,

    /// <summary>
    /// Hardening, server-wide: the generic backstop for an action that binds <c>parentId</c>, <c>seriesId</c> or
    /// <c>seasonId</c> without a row of its own: 404 when the id resolves without a user but not with the caller. The
    /// manifest names the Jellyfin.Api actions; since 0.9.7 the rule table gives it at run time to every action outside the
    /// manifest (another plugin's, or a Jellyfin.Api action a later Jellyfin adds) that binds one of the three.
    /// </summary>
    GenericBackstop,

    /// <summary>The generic backstop does not apply to this action; the manifest entry's review says why.</summary>
    BackstopExempt,

    /// <summary>Hardening, <c>Strict</c> only (Access Roles 1.1): token-carrying streams of invisible items answer 404.</summary>
    StrictStream,
}
