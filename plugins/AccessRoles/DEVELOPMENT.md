# Access Roles

A Jellyfin plugin that gives non-admin users roles: a role locks sections of their settings and grants libraries, and
the server enforces both, for the web client and for any other client. This is its technical documentation; the user
guide is [README.md](README.md).

**Version 1.0.3.0** for Jellyfin 10.11.5 (`net9.0`). It writes managed users' library access, enforces the settings
locks, hides the locked settings in the web client, keeps the Dashboard's data and server calls from the non-admins whose
role blocks them, and hardens the server for every non-admin (configuration reads, the restart, the public user list, and
API answers that leaked items of libraries a user may not open). Its admin page has the tabs Overview, Roles, Users,
Libraries, Audit, Settings, Data and Diagnostics; every change's preview is the dry run of the write itself. Every lock is
enforced (`/Me`'s `NotEnforced` is empty). The release notes are in [build.yaml](build.yaml)'s `changelog`.

**Administrators and API keys are never restricted** by any lock or rule, so every enabled administrator can always reach
the plugin's page and API. Only library roles apply to administrators (their library access is written like anyone's).

## Installing, upgrading and going back

The user README has the short version; this is the careful way for a server that already has users and libraries.
Paths are Jellyfin's Windows defaults (`C:\ProgramData\Jellyfin\Server`); use your own.

### Installing

1. **Jellyfin Enhanced**: 1.0 reviewed JE 12.14's actions. After step 4 the *Diagnostics* tab's *Rule table* should
   read, in its *Jellyfin Enhanced* row, "Installed: 7 saves follow its lock, 46 actions not covered (0 not reviewed)".
   With another JE version, look at anything not reviewed before step 7.
2. **Back up**: Dashboard → *Backups*; stop Jellyfin; copy `plugins\configurations` aside.
3. **Install**, Jellyfin stopped: extract the release zip into `plugins\Access Roles_<version>`, or from a clone run
   `scripts\Install-RealServer.ps1 -Plugin AccessRoles -Version 1.0.3` (first with `-WhatIf`). It builds the tag in
   Release, takes a cold backup under `$HOME\JellyfinBackups\<yyyyMMdd-HHmmss>-before-AccessRoles-<version>` (with
   `RESTORE.txt`, the two ways back) and installs the folder checked by SHA-256 ([scripts/README.md,
   "Install-RealServer.ps1"](../../scripts/README.md#install-realserverps1)).
4. **Start Jellyfin**: the plugin is *Active*; nobody is managed, only the server-wide rules for every non-admin are on.
5. **Enforcement off, at once**: *Settings* tab, untick *Enforce the roles*, save. While the server runs this only
   pauses; don't restart before step 7 (from the next start, off makes the plugin inert).
6. **Import current setup** (Overview), *Also manage users who see every library* unticked: the preview must say "No
   change for N of the N users the import would assign"; if any user shows a change, stop. Then apply it.
7. **Check, then enforcement on**: *Preview the sync* must say "Its first run writes nobody's library access, and leaves
   nobody out.", and *Libraries* must mark nobody. Then tick *Enforce the roles*: the sync runs 2 s later and writes
   nothing (`LibrarySyncResumed`, no `PolicySynced`).
8. **One restricted user**: give them a role and sign in as them in each client they use: the locked entries are gone, a
   locked page goes to Home with "This page is managed by your server administrator.", their libraries are the role's.
9. **After a day**: the *Audit* tab's `RequestsBlocked` by rule. If a native client misbehaves on configuration reads,
   add the key it needs to the allow-list or choose `Redact`.

### Upgrading

1.0.0, 1.0.1 and 1.0.2 share one data format (schema 1), read as it is in either direction; nobody's access or locks
change, and enforcement can stay on. Back up, stop Jellyfin, replace `Access Roles_<old>` with `Access Roles_<new>` (one
Access Roles folder only, or the script, which prints "Removed Access Roles_<old>"), start, and check: the version
*Active*, Diagnostics' data file *Ok* and *Rule table* as before, the same roles, users and snapshots, no `PolicySynced`
since the start, nobody marked on *Libraries*. A sync held by a 1.0.0 restore is confirmed as before (a hold file without
`Owner`). 1.0.0's *Remove references…* dropped a deleted library's exclusions, so after a 1.0.0 purge a library created
again under the same name is granted by those *All except…* roles: check its card. **While 1.0.0 runs, don't use *Remove
references…***: upgrade first.

### Going back

No data needs undoing between 1.0.x versions: stop Jellyfin, put `Access Roles_<old>` back in place of the new folder,
start. **To pause**, untick *Enforce the roles* or use the emergency stop. **To remove**, while it is enabled: *Release
all locks* with *Also restore each user's original library access* ticked, then *Uninstall*, then restart;
`data\accessroles` and the settings file stay. **Never *Disable* it on 10.11.5** ([Lockout and
recovery](#lockout-and-recovery)). To undo a scripted install, release the locks first, then `RESTORE.txt` with Jellyfin
stopped (A: the plugin and its settings deleted, `data\accessroles` moved aside as
`data\accessroles.removed-<yyyyMMdd-HHmmss>`, never deleted; B: the whole backup, losing what the server recorded since).

## Architecture

- **Identity**: GUID `7cbc1019-49a5-4b97-8aa2-279de946ef88`, assembly `Jellyfin.Plugin.AccessRoles`, routes
  `/AccessRoles`, web prefix `jfar`, schema prefix `AccessRoles`, data folder `data/accessroles`, `targetAbi` 10.11.5.0.
  On 12.x the kit's ABI guard leaves it inert (endpoints 503 with the reason).
- **Components**: `Access/EffectiveRulesResolver` (the rules); `Policy/PolicyWriter` (the only code of the suite that
  writes a user's policy); `Sync/LibrarySyncService`, `Sync/LibraryStructureFilter`, `Access/LibraryLifecycle` (the
  library sync); `Policy/UserPolicyFilter` (the Dashboard save's pre-merge and the sign-in hook);
  `Enforcement/AccessRulesActionFilter` (the one global MVC filter, order −10000: settings locks, lock rules and
  `Enforcement/ServerHardening`, by [the action manifest](#the-action-manifest)'s rule table); `Web/AccessRolesHead` and
  the ClientScript (the web guard); the admin controllers and `AdminScript`.
- **The data file** `data/accessroles/accessroles.json` (schema 1; the kit's JSON store with a `.bak` copy, read-only on
  a newer version's file, unknown fields kept: [kit/README.md, "JSON stores"](../../kit/README.md#json-stores-storage)):
  roles, assignments, default roles, `KnownLibraries`, `PendingRenames`, the ledger of users' original library settings
  (with a pending `Release`, `LastWrite`, `DefaultRolesUtc`), `ConfirmedNoFullAdminUtc`, `PreferenceMirror` and users'
  names. Enum values a newer build wrote are kept by name (`Records.UnknownValues`); a user with one has every settings
  section locked and is never written. An unreadable or missing file is read-only (`ReadOnlyDataUnreadable`,
  `ReadOnlyDataMissing`); the store never starts empty on its own ([Degraded mode](#lockout-and-recovery)).
- **Other files**: `audit/audit-<yyyyMM>.jsonl`, `snapshots/`, `library-sync-hold.json`, `degraded.json`, `SAFE-MODE`.
- **The audit log**: one JSON line per change (`V`, `Seq`, `Utc`, `Actor` `Admin|ApiKey|System`, `Action`, the target,
  `Details`), one file per UTC month, numbered across files; the actions are `Audit/AuditActions`. A failed write is
  logged and counted (`Audit.WriteErrors`) and never undoes its change.
- **The daily task** *Access Roles: Maintenance* (03:50): a snapshot (`SnapshotsToKeep`), the kit's user reconciliation,
  and the audit retention (months older than `AuditRetentionMonths`, then the oldest while over 50 MB; never the current
  month).
- **Deleted users**: `DeletedUserPolicy` is fixed to `Delete`: the assignment and ledger entry go (`UserDeleted`). *Remap
  by name* and *Apply deleted-user policy* are the kit's ([kit/README.md, "Users and
  data"](../../kit/README.md#users-and-data-users-export-and-import-in-storage)).
- **Endpoints**: `GET /AccessRoles/ClientScript` and `GET /AccessRoles/AdminScript` (anonymous and static), `POST
  /AccessRoles/Diagnostics/ClientHealth` and `GET /AccessRoles/Me` (signed-in users); everything under
  `/AccessRoles/Admin/` is `RequiresElevation` (401 anonymously, 403 for non-admins). 35 actions and 63 schemas, every
  one `AccessRoles…` (`ApiConventionTests`).
- **Diagnostics** (`GET Admin/Diagnostics`): the build, `Enforcement` (what is active, counts and cost, the lock snapshot,
  `Degraded`), the data file and `Records`, `LibrarySync`, `RuleTable`, the web client's reports and the server's users.

## Settings

In the plugin's XML configuration, edited on the Settings tab. A value this build doesn't know is read as the default.

| Setting | Default | What it does |
|---|---|---|
| `EnforcementEnabled` | true | The sync, the hooks, the locks and the hardening. Off while running: paused; off at a start: inert. |
| `LibrarySyncEnabled` | true | The library sync. |
| `ReconcileIntervalMinutes` | 5 | 1 to 60: the sync's backstop run. |
| `DriftPolicy` | `Reapply` | `Reapply` or `ReportOnly` ([the library sync](#the-library-sync)). |
| `FirstLoadMode` | `HideUntilKnown` | The web guard's first load. |
| `BlockedNavigationNotice` | `Toast` | `Silent`: no toast when a locked page is left. |
| `ShowManagedNoticeOnUserPages` | true | Administrators' banners on managed users' stock pages. |
| `LibraryHardening` | `Standard` | `Standard` or `Off`; `Strict` behaves as `Standard`. |
| `NonAdminConfigurationReads` | `AllowList` | `AllowList` or `Redact`. |
| `ConfigurationReadAllowList` | `encoding` | Keys a non-admin may read under `AllowList`. |
| `BlockNonAdminRestart` | true | Only administrators and API keys may restart. |
| `TrimPublicUsers` | true | The public user list keeps only the login page's fields. |
| `MirrorPreferenceLockToPolicy` | false | The optional native mirror ([Lockout and recovery](#lockout-and-recovery)). |
| `SnapshotsToKeep`, `AuditRetentionMonths` | 7, 12 | Daily snapshots; audit months (0 or less keeps all). |
| `EnableWebUi`, `ReportWebAnchorProblems` | true | The web part; anchor reports in Diagnostics. |

## The roles API

Every route under `/AccessRoles/Admin/` resolves every id (users through the server, roles in the store, libraries against
`GetVirtualFolders()`). Writes run their checks inside the store's one writer and take:
- `?dryRun=true`: the same checks and answer, nothing saved (also on a read-only store, where a save is 503). A dry run
  the save would refuse for what the change does carries the analysis as its `Preview` extension, so a write's dry run is
  its preview.
- `?ifPreview=`: the dry run's `PreviewToken` (each affected user's resulting state, access, libraries, locks and
  leave-management choice, hashed). The save works the change out again and, when it now does anything else, answers 409
  `Changed` naming the users whose result differs, writing nothing. **A save without `ifPreview` is accepted
  unchecked**; the admin page always sends it.
- `?onUnmanage=Restore|Keep|GrantAll` and `?confirmNoFullAdmin=true` (below).

They answer `AccessRolesChangeDto` (what was saved, the users affected, the per-user preview, the audit line numbers).
Refusals are ProblemDetails with a `Code` and the `Users` concerned.

| Method | Route (`Admin/…`) | What |
| --- | --- | --- |
| GET | `Roles`, `Roles/{id}` | Roles with member count, default flag, the *Dashboard* row and `MembersToken`. |
| POST | `Roles` | A role from scratch or a preset (`Kid`, `Guest`, `Standard`). 400 `EmptyOnlyRole`, 409 `DuplicateName`. |
| PUT | `Roles/{id}` | The whole role; `IfUpdatedAt` required (409 `Changed`); optional `IfMembers`. |
| DELETE | `Roles/{id}` | `?reassignTo=` gives the members another role; 409 `DefaultRole` while default. |
| PUT | `DefaultRoles` | The roles new users get. |
| GET | `Users` | Every user with roles, state (`NotManaged`, `Managed`, `Excluded`, `CannotManage`, `UnknownValue`), lock count, `PendingWrite`; `?roleId=&state=`. |
| PUT | `Users/{userId}` | The assignment: roles, lock overrides (`Lock`/`Unlock`), library mode (`Inherit`/`All`/`Only`) with `Add`, `Remove` and `ConfirmNoLibraries`, state; `IfUpdatedAt`. An empty assignment is removed. |
| POST | `Users/Bulk` | Roles given to and taken from 1 to 1000 users; optional `IfUpdatedAt`. |
| POST | `Users/{userId}/Unmanage` | `{Choice}` (required). |
| GET | `Users/{userId}/Effective` | Every lock with its source, visible libraries with every source, hidden ones with the reason (`RemovedForUser`, `NotGranted`, `Recreated`), native access, the pending write, drift, `LastWrite`, warnings; `?simulateRoleIds=`. |
| POST | `Preview` | A proposed state against the stored one (the page uses dry runs instead). |
| GET | `Libraries` | Every library with granting roles, `InNoRole`, `Orphaned`, `Recreated`; who opens it now with a `Mismatch` (`Drift`, `NoLibraries`, a pause, `Pending`), `Purgeable`. |
| GET | `Audit`, `Audit/Export` | Newest first (`startIndex`, `limit` 1 to 200, `userId`, `action`, `fromUtc`, `toUtc`, `rule`, `before`: the `Seq` of the last line shown); the export as JSON Lines. |
| POST | `Users/Remap`, `Users/ApplyPolicy` | The kit's tools; a remap that writes needs its dry run's `PlanToken` as `ifPlan`. |
| POST | `Sync`, `Libraries/{id}/GrantAgain`, `Libraries/{id}/Purge` | [The library sync](#the-library-sync). |
| POST | `ImportExisting` | [Import current setup](#the-user-policy-hooks-default-roles-and-the-import). |
| GET/PUT | `Settings` | The Settings tab ([the admin page](#the-admin-page-overview-libraries-audit-settings-data-and-diagnostics)). |
| POST | `Degraded/Resolve`, `ReleaseLocks` | [Lockout and recovery](#lockout-and-recovery). |
| GET/POST | `Export`, `Import`, `Snapshots`, `Snapshots/Restore`, `Maintenance/Run` | [Export, import and restore](#export-import-and-restore). |

**The rules** (`Access/EffectiveRulesResolver`):
- **Locks**: locked if any of the user's roles locks it; the user's own `Lock` or `Unlock` wins. No lock applies to an
  administrator, an `Excluded` or an unmanaged user.
- **Libraries**: the start is every library (user mode `All`), nothing (`Only`, its `Add` list supplying them), the union
  of the library roles' grants (`Inherit`: `All` and future libraries, `AllExcept` expanded against the libraries now,
  `Only` the listed ones), or the ledger's original access (`Inherit` without a library role but with `Add` or `Remove`);
  then `(start ∪ Add) − Remove`, `Remove` beating every grant. New libraries go to `All` and `AllExcept` users, never
  `Only` ones.
- **Re-created libraries** (deleted and created again under the same id) are granted by no `Only` list, `Add` list or
  original access until the admin grants them again (hidden as `Recreated`); `AllExcept` and `Remove` keep leaving the
  id out.
- A managed result with no library is refused (409 `NoLibraries`) unless the mode is `Only` with nothing added and
  `ConfirmNoLibraries`. A user with a legacy `BlockedMediaFolders` list is `CannotManage` (locks apply, libraries aren't
  managed).

**Leaving library management** (the last library role removed, the state set to `NotManaged` or `Excluded`, `Unmanage`)
needs `onUnmanage` (400 `UnmanageChoiceRequired`): `Restore` (the ledger's original access, deleted and re-created
libraries left out; 409 `NoLedgerEntry` without one), `Keep` (audited `LibraryAccessFrozen`) or `GrantAll`. The ledger
records a user's native access when a save first makes their libraries managed, keeps it while the user has an
assignment (also through a pause), and drops it when nothing is left to write or the user is deleted. `Restore` and
`GrantAll` leave a pending `Release` with the `Baseline` it was computed against. A change that leaves no enabled
administrator with every library is 409 `NoFullAdministrator` until `confirmNoFullAdmin=true` (the lockout guard).

## The library sync

**What it writes**: for every user whose libraries the rules manage, administrators included, the resolver's result in
two fields only: `EnableAllFolders` and `EnabledFolders`. Unmanaged users are never written.

**The writer** (`Policy/PolicyWriter`; a test checks no other type calls `UpdatePolicyAsync`, `UpdateUserAsync` or
`UpdateConfigurationAsync`):
1. Under the user's lock (`UserPolicyLocks`, shared with the user policy filter; 10 s, then skipped) it reads the full
   policy the server enforces. It writes nothing when the library fields aren't the ones the run planned against
   (`DriftDetected`, or a release dropped), and skips a user who already has them.
2. It compares the cache with the stored row (`Policy/DatabaseCommittedUsers`, the server's `JellyfinDbContext`,
   `AsNoTracking`) over every `UserPolicy` field; on a difference it builds again 250 ms later, on a second it aborts
   (`WriteAborted`) and retries 30 s later. When they disagree it builds from the stored row (10.11.5 caches a save's
   entity before saving).
3. It sets the two fields and `EnableLyricManagement` (`GetUserDto` never fills it, and a write without it clears it). It
   never enables a user (jellyfin/jellyfin#16298) and never changes `IsAdministrator`.
4. It writes the **full** policy (a partial one resets 29 to 32 fields) and checks again about 5 s later: when the old
   access is enforced again because of the 10.11.5 sign-in race, it writes the stored row back (`RevertedAfterWrite`); an
   admin's edit back is left to the next run's rules.
5. When `UpdatePolicyAsync` throws (a concurrent save came first), it writes again 1 s later from the stored row, keeping
   the fields the other save changed (`RetriedAfterFailedSave`).

**When it runs** (`Sync/LibrarySyncService`; runs never overlap): at startup; 2 s after a saved change of roles or
assignments; at once, 2 s and 10 s after a library structure call (`Sync/LibraryStructureFilter`); 2 s after a new
library's `ItemAdded`; every `ReconcileIntervalMinutes`; on `POST Admin/Sync`; after a settings save that lets it write
more.

**Drift** is a managed user's native access that is neither the result nor the ledger's `LastWrite`. `Reapply` writes
the result again. `ReportOnly` keeps the admin's edit and shows it until reverted with `POST Sync?reapplyDrift=true`, but
applies changes in the safe direction (`Sync/DriftDecision`): it removes deleted libraries, re-created ones nothing
grants, and ones the rules no longer give; new grants are reported, not applied. `DriftDetected` (once per situation)
lists `Kept`, `Applied`, `NotApplied`. A no-library result nobody chose is its own state (`NoLibraries`), never drift.

**Refusals** (audited `SyncSkipped`): a result with no library that wasn't chosen while the user still has libraries
(when library deletions already left the user nothing, it is written: `UsersWithoutLibraries`); and the lockout guard
(`NoFullAdministrator`) unless confirmed.

**Releases**: a pending `Restore` or `GrantAll` is written while the user's native access still is its baseline
(`LibraryAccessRestored`); otherwise it is dropped without a write (`ReleaseDropped`), keeping the admin's edit.

**Paused** (nothing written; `LibrarySyncPaused`/`Resumed`): while `SAFE-MODE` exists, `EnforcementEnabled` or
`LibrarySyncEnabled` is off, or the store is read-only or degraded; and **after the data file was restored from a
snapshot** (`StoreRestored`). Then nothing is written until an admin has looked at `POST Sync?dryRun=true` and confirmed
with `POST Sync?confirmRestore=true&ifPlan=<its PlanToken>` (409 `DryRunRequired`, 400 `IfPlanRequired`). The hold
lives in `library-sync-hold.json` with its restore's `Owner` nonce; a confirmation lifts only the hold its dry run saw.

**The library lifecycle** (`Access/LibraryLifecycle`, measured on 10.11.5): existence only from `GetVirtualFolders()`,
ids from `GetNewItemId` of the library's folder, created times from the folder. A rename waits until the new name has its
item, then re-points roles, overrides, the ledger and releases (`LibraryRenamed`); the backstop finds a rename by equal
folders. A deleted library (`LibraryRemoved`) leaves managed users' access at the next run and becomes `Orphaned`; one
created again under the same id is `LibraryRecreated`. Previews resolve as the sync does, so none shows a library the
sync won't write.

| Method | Route (`Admin/…`) | What |
| --- | --- | --- |
| POST | `Sync` | Runs the sync: per user `Action` (`Write`, `Release`, `DropRelease`, `Drift`, `Skip`), `Reason`, `Native`, `Target`, `Added`, `Removed`, `Outcome`; the lifecycle's changes. `?dryRun`, `?confirmNoFullAdmin`, `?reapplyDrift`, `?confirmRestore`, `?ifPlan` (409 `Changed`, nothing changed), `?userId=` (*Revert this user*). 503 for a real run on a read-only store. |
| POST | `Libraries/{id}/GrantAgain` | Clears a re-created library's mark; 409 when not re-created; `?dryRun=true`. |
| POST | `Libraries/{id}/Purge` | Removes a deleted library's grants. Its exclusions stay (an `AllExcept` list, a user's `Remove`), and while one names it the library stays `Orphaned`; 409 while it exists or when only exclusions name it; `?dryRun=true`. |

**Troubleshooting (10.11.5)**: "A sign-in failed with an error at the moment a policy changed" is a core race: the user
signs in again; the check about 5 s later writes the stored row back. "An admin's policy save failed while the sync
wrote" is the same race between two saves: the loser answers 500 and the admin saves again; when Access Roles' save lost,
it rewrites keeping the admin's fields.

## The user policy hooks, default roles and the import

**An admin's Dashboard save** (`POST /Users/{id}/Policy`) goes through `Policy/UserPolicyFilter` (a global filter at
−10000): it takes the user's policy lock (5 s, then the save goes on: fail open), asks the sync what its next run would
write if the posted access were the user's, and puts those two library fields into the post (the roles' result under
`Reapply`, only the safe-direction removals under `ReportOnly`). Every other field is the admin's. After the core saved
it, it is recorded as Access Roles' write and audited with the admin as actor (`ManualEditOverridden`, with what was
undone: "Manual edit to Library access replaced by roles (added: Movies B)"). A pre-merge that throws lets the save
through, audited `DriftDetected` `HookFailed`.

**A sign-in** (`AuthenticateUserByName`, `AuthenticateUser`, Quick Connect, and any plugin action answering an
`AuthenticationResult`, such as an SSO plugin's): a managed user's access is corrected by a one-user run before the
response leaves. A non-administrator's sign-in is refused with 503 and `Retry-After`, its token revoked
(`SignInRefused`: fail closed), when the server enforces more libraries than the rules give and the correction failed,
or while the sync is held. An administrator's sign-in is never refused.

**Default roles**: a user Jellyfin creates (*Add user*, the API, an LDAP or SSO plugin) gets them at once
(`UserCreatedDefaulted`), with "no libraries" recorded as the original access. **Nothing writes the user while the
request creating them runs**: the core saves the new user's password after the event, and a policy write in between
failed that save. So the user is "being created" until the response completes (60 s at most), and runs follow at 3 s,
10 s and 30 s. A new user whose roles give no library gets none (fail closed).

**Import current setup** (`POST Admin/ImportExisting`, `?dryRun=true`): one role per distinct library access among
non-admins, named after its libraries (`Movies + Kids`), no locks, assigned. Users who see every library stay unmanaged
unless `manageUsersWithEveryLibrary=true`. `EffectiveChanges` must be empty: whoever no role would give exactly their
access isn't imported (`AccessWouldChange`), nor are administrators, legacy blocked-folder users and users with no
library; users on the login screen and Live TV managers are warned about. 409 `AlreadyManaging` once anyone is managed.

**Troubleshooting**: "Library access I ticked on the Dashboard doesn't stay": the user is managed (`ManualEditOverridden`);
change their roles or set them `Excluded`. "A sign-in answered 503": `SignInRefused` says why; sign in again after
`Retry-After`, and confirm a hold on the Overview.

## The settings locks

**Who is restricted**: a signed-in non-administrator whom a role or an own override locks a section. `Excluded` and
unassigned users meet no lock; anonymous callers meet no settings rule; `EnforcementEnabled=false` or `SAFE-MODE` lets
everything through.

**What the server does** (`Enforcement/AccessRulesActionFilter`; each action's rule from the action manifest by
controller and action name, so a route alias or BaseUrl changes nothing):

| Section | Refused (403 `urn:jellyfin-plugin-kit:locked`) | Merged (stored value kept: 204) |
| --- | --- | --- |
| Profile | the password, the user image (all six routes), a name change through `POST /Users` or `/Users/{id}` | `EnableLocalPassword` |
| Display (account) | – | `DisplayMissingEpisodes`, `DisplayCollectionsView`; usersettings `useEpisodeImagesInNextUpAndResume`, `dashboardTheme` |
| Home | – | `OrderedViews`, `LatestItemsExcludes`, `MyMediaExcludes`, `GroupedFolders`, `HidePlayedInLatest`; usersettings `homesection*`, `tvhome`, `landing-*` |
| Playback | – | `AudioLanguagePreference`, `PlayDefaultAudioTrack`, `EnableNextEpisodeAutoPlay`, `RememberAudioSelections`, `RememberSubtitleSelections`, `CastReceiverId`; usersettings `skipBackLength`, `skipForwardLength` |
| Subtitles | – | `SubtitleLanguagePreference`, `SubtitleMode` |
| Quick Connect | `POST /QuickConnect/Authorize` | – |
| Jellyfin Enhanced | JE's writes under `user-settings/{userId}/` but the bookmark ones, and `POST spoiler-blur/user-prefs`, when JE is installed | – |
| Display (this device), Controls | – (kept on the device: hidden only, by the web guard) | – |

- **The merges** are on the user configuration posts and `POST /DisplayPreferences/{id}` for the web client's
  `usersettings` document. Locked keys are taken out of the post in any case and the stored values added back, so a key
  the post left out is kept too. jellyfin-web saves whole pages, so a merge, not a refusal, keeps the unlocked sections'
  changes; a locked change is silently undone, and the `X-AccessRoles-Kept` header names the sections when it differed.
- **Not locked, by review**: JE's bookmarks, Hidden Content, per-item Spoiler Guard marks and reviews (the user's own
  state, item by item).
- **Failure: fail closed.** A rule that throws, or a data file that can't be read, refuses with 403 "Settings changes are
  temporarily unavailable", counted (`FilterErrors`); administrators', API keys' and anonymous requests pass. Actions
  without a rule (280 of Jellyfin.Api's 390) pass at the cost of one lookup.
- **Counted and audited**: every refusal and revert per user, rule (`Settings.<section>`), action and outcome
  (`Refused`, `Reverted`, `FailedClosed`) in 10-minute windows, each window one `RequestsBlocked` audit line.
- **`GET /AccessRoles/Me`** (signed-in users; `no-store`): the caller's own locks, `AllSettingsLocked`, `NoLibraries`
  with its message, a `RulesVersion` hash, and `NotEnforced` (empty). Nothing of another user.
- **The rule table's self-check** (`RuleTableSelfCheck`, Diagnostics `RuleTable`): the filter registered; every rule
  bound to a routed action with its reviewed shape (all 110 on 10.11.5); unreviewed Jellyfin.Api actions; JE's locked
  saves and its uncovered writes (46 on 12.14, 18 of them admin-only); plugin controllers that take a Jellyfin
  controller. `EnforcementActive` is false, with a banner, when the filter isn't registered or a row doesn't bind.
- **Native apps** (not measured with each): a merged save answers 204, so an app shows its change until it reads the
  settings again; settings an app keeps on the device can't be locked.

## The web guard

The web client hides what the server refuses or merges. **It is cosmetic**: the filter enforces every lock for every
client. The kit's web side is in [kit/README.md, "Web client"](../../kit/README.md#web-client-web-webembeddedscript).

- **The head fragment** (`Web/AccessRolesHead`) is inserted inline before `</head>`, so it runs before jellyfin-web's
  bundles. Its CSS, built from `Web/anchors.json`, hides the locked entry points while `<html>` carries
  `jfar-lock-<section>`; `jfar-pending` hides every Settings entry point. Its script sets the classes from the browser's
  cache (the last user's cached `/Me` under `jfar:rules:<user id>`), never throws, and lifts its own `jfar-pending` after
  3 s.
- **The first load** (`HideUntilKnown`): `jfar-pending` until `/Me` answers (3 s at most) for a user the cache says is
  managed, or, uncached, while anyone is managed. A sign-in or user switch sets it again; a sign-out removes the classes.
- **The route guard** (measured against jellyfin-web 10.11.5's router): a locked section's page (`/userprofile`,
  `/quickconnect`, `/mypreferencesdisplay`, `/mypreferenceshome`, `/mypreferencesplayback`, `/mypreferencessubtitles`,
  `/mypreferencescontrols`, normalised) is replaced by `#/home`, with "This page is managed by your server
  administrator." (none with `Silent`). Locked links stop in the capture phase. The Settings menu stays (the TV
  layout's only Sign out).
- **The pages**: the user menu's hidden items get `aria-disabled`; the Settings page says "Settings are managed by your
  administrator" when every section is locked; the Display page's account fields are disabled; Home's empty-library text
  becomes `/Me`'s message.
- **A merged save** with `X-AccessRoles-Kept` shows "Some of these settings are managed by your server administrator, so
  they weren't changed: Home screen."
- **Administrators** (`ShowManagedNoticeOnUserPages`): on a managed user's stock profile, Library access, parental control
  and password tabs a banner names the roles and lists the locks; on *Add user* it names the default roles.
- `EnableWebUi=false` serves `index.html` without the fragment and the script.

Every jellyfin-web selector is a declared anchor (`Web/anchors.json`, 33), reported to Diagnostics when missing
([kit/README.md, "Coupling policy and
health"](../../kit/README.md#coupling-policy-and-health-diagnostics-kitanchors-kithealth)).

## The admin surfaces and the server-wide hardening

`Enforcement/ServerHardening` runs in the same global filter after the settings rules, for every caller that is neither
an administrator nor an API key. A refusal is 403 `urn:jellyfin-plugin-kit:locked`; an answer that hides an item, library
or user is the action's own empty result or 404, or its result rewritten on new objects. Enforcement off and `SAFE-MODE`
switch it off.

**`AdminSurfaces`** (per role, *Block Dashboard data and server calls*): 403 for 26 actions: the Live TV setup writes and
reads (tuner hosts, listing providers, channel mappings), the library pokes (`POST /Library/{Media,Movies,Series}/Updated`),
the remote metadata and image searches, and `GET /Libraries/AvailableOptions`. The DVR's recordings stay with the native
`EnableLiveTvManagement`. `GET /System/Info` loses its seven obsolete server-path fields for every non-admin under
`LibraryHardening` `Standard`, and for this lock's callers under `Off`.

**`UserDirectory`** (per role): `GET /Users` lists only the caller; another user's `GET /Users/{id}` is the core's 404.

**Server-wide, for every non-admin**:
- **Configuration reads** (`NonAdminConfigurationReads`): `AllowList` refuses `GET /System/Configuration` and every named
  key not in `ConfigurationReadAllowList` (`encoding`, which the player reads for ASS subtitles); `Redact` answers every
  key. Either way the answer is a **copy** with secrets and server paths stripped (certificate, tuner, listing-provider
  and OpenSubtitles credentials, the database connection string, encoder, transcoding, cache and metadata paths, path
  substitutions; the list is `Enforcement/ConfigurationReadGuard`), used only when it serialises to the same bytes. In
  `Redact` another plugin's type is refused. **The media's paths are not stripped**: stock 10.11.5 gives an item's `Path`
  to whoever can see it.
- **Restart** (`BlockNonAdminRestart`): `POST /System/Restart` is 403 "Only an administrator can restart this server."
  for anyone else, including an anonymous LAN caller (the core's `LocalAccessOrRequiresElevation` admits one). Shutdown is
  already the core's `RequiresElevation`.
- **The public user list** (`TrimPublicUsers`): `GET /Users/Public` keeps `Id`, `Name`, `ServerId`, `PrimaryImageTag`,
  `HasPassword`, `HasConfiguredPassword`, `HasConfiguredEasyPassword`.
- **Library leak hardening** (`LibraryHardening` `Standard`): each id is judged once per request by the kit's
  `LibraryAccess.Visibility`. Latest with an invisible `parentId` is empty; `Items?ids=` keeps visible ids; an invisible
  `parentId` of Items, Trailers and Resume is 404; Upcoming, NextUp and the aggregates (Genres, Studios, Artists, Years,
  Filters, Search/Hints…) are empty; a playlist's hidden members are dropped; the People search keeps names with an item
  in the caller's libraries by **one database query per page**; playback reports of a hidden item 204; `GET /Sessions`
  hides other sessions' hidden items; trickplay 404; and the backstop: an invisible `parentId`, `seriesId` or `seasonId`
  of any action outside the manifest that binds one (another plugin's, a later Jellyfin's) is 404.

**Failure: fail closed.** A hardening rule that throws refuses with 403 "This request is temporarily unavailable: Access
Roles couldn't check it, so it was refused. Try again in a moment." (`FailedClosed`).

**Counters and cost**: `Refused` and `Hidden` per user, rule (`Locks.AdminSurfaces`, `Locks.UserDirectory`,
`ServerWide.ConfigurationReads`, `ServerWide.Restart`, `Hardening.<rule>`), action and window, into `RequestsBlocked`;
Diagnostics `Cost` gives each rule's own time per request (p50, p95, max over the latest 256).

**Measured** on a `scale` sandbox (12,019 movies, 50,004 episodes): every budget holds. 200 `Items?ids=` at most 4.12 ms
p95 (budget 20 ms); People 100 at most 5.02 ms p95 (budget 50 ms); the other rules under 0.1 ms. A per-name People check
took 0.7 to 0.95 s p95 for 100 names, which is why the rule is one query per page.

**Native apps**: a configuration key other than `encoding` is a 403; add it to the allow-list or use `Redact`.

## The admin page: Roles and Users

The dashboard page *Access Roles* is the kit's dashboard module: `configPage.html` imports `GET /AccessRoles/AdminScript`
and mounts it ([kit/README.md, "Page hosting"](../../kit/README.md#page-hosting-webpages-kitpages-kitnav)). Every
server value is set as text; dialogs, focus and toasts are the kit's. Banners from Diagnostics sit above the tabs; every
write is disabled while the store is read-only.

- **Roles**: the default roles, each role with members, locks and libraries; *New role* from Kid, Guest, Standard or
  blank. The editor: name, description, the nine sections (marked server-enforced or hidden only), *Block Dashboard data
  and server calls*, *Hide other users*, and libraries (not managed, all, all except, only).
- **Users**: each user with roles, state and badges; *Assign roles…*, *Remove roles…* (up to 1000). The panel: roles,
  state, an override per lock, the library mode with *Add*/*Remove*, the drift note with *Revert this user…*, the
  effective access, *Unmanage user…*.
- **Every change is previewed**: the preview is the dry run of the very request Save sends. Per affected user: sections
  newly locked or unlocked, libraries added or removed, whether the sync will write. Leaving management asks the choice
  first. A 409 `Changed` saves nothing and offers *Reload and keep my changes*.
- **Keyboard, TV and touch**: WAI-ARIA tabs; on TV every control is in the D-pad's reach and Back closes a dialog; on a
  phone 44 px controls. `#/configurationpage?name=AccessRoles&tab=Users` opens a tab.

## The admin page: Overview, Libraries, Audit, Settings, Data and Diagnostics

- **Overview**: chips for what enforces (or why not), the web UI, the sync, drift, users with no library,
  administrators, other policy managers (SSO, LDAP and the like), JE, filter errors, degraded enforcement; *Server-wide
  behaviours*; *Import current setup* while nobody is managed; *Preview the sync* and *Confirm and resume the sync* while
  held; *Before disabling or deleting Access Roles*; Degraded's *Stop locking them*.
- **Libraries**: each library, who opens it now and why, and warnings: in no role; renamed; deleted, until **Remove
  references…**; re-created, until **Grant again…**.
- **Audit**: 50 lines a page with *Load more*, filtered by user, action, dates and rule; *Download*.
- **Settings**: *Review* sends what changed as `PUT Settings?dryRun=true`; allow-list keys that carry secrets
  (`network`, `livetv`, `database`, `subtitles`) need their box (`confirmSecretKeys`). *Save* sends the `PreviewToken`.
  Audited `SettingsChanged`.
- **Data**: the data file, *Export*, *Check the file*, *Import*, the snapshots with *Restore*.
- **Diagnostics**: the status tables, the rule table and times, the web client's reports, the whole document as text;
  *Remap by name*, *Apply deleted-user policy*, *Run maintenance now*.

## Lockout and recovery

Nothing here ever restricts an administrator or an API key, so every enabled administrator can always reach the page and
API, also one whose library role gives it one library.

**Degraded mode**: a start whose data can't be trusted (the store recovered empty, or read-only without a usable
document).
- The managed users are read leniently from the stored file (only `Assignments[].UserId` and `State`), else its newest
  readable copy (`.bak`, the snapshots, the pre-migration copies) and the mirror's records, and only they are locked:
  every server-enforced section, `AdminSurfaces` and `UserDirectory`. With nothing readable a read-only start locks every
  non-admin.
- A degraded start that locks anyone writes `degraded.json`; a later start keeps them locked until an admin confirms on
  the Overview (*Stop locking them*: `POST Admin/Degraded/Resolve`, a dry run, then `ifPreview`; audited
  `DegradedModeEntered`/`Ended`), so a store put back empty never switches locks off unseen.
- A newer schema's file is read-only but its own assignments are enforced. An unreadable file with a readable snapshot or
  `.bak` is restored from it (the sync held).

**The emergency stop**: with the server stopped, create `SAFE-MODE` in the data folder or set
`<EnforcementEnabled>false</EnforcementEnabled>`, and start. The plugin is then inert: only its admin page (which says
which is in effect), every other action 503. Inert, it doesn't put back what the mirror turned off. Delete the file (or
set the setting back) and restart to end it.

- Windows: `C:\ProgramData\Jellyfin\Server\data\accessroles\SAFE-MODE` and
  `C:\ProgramData\Jellyfin\Server\plugins\configurations\Jellyfin.Plugin.AccessRoles.xml`
- Linux packages: `/var/lib/jellyfin/data/accessroles/SAFE-MODE` and
  `/var/lib/jellyfin/plugins/configurations/Jellyfin.Plugin.AccessRoles.xml`
- Docker (the `/config` volume): `/config/data/accessroles/SAFE-MODE` and
  `/config/plugins/configurations/Jellyfin.Plugin.AccessRoles.xml`

**The optional native mirror** (`MirrorPreferenceLockToPolicy`, off by default because the flag also blocks the user's
own watched states and favourites): Jellyfin's `EnableUserPreferenceAccess` is set false for a managed non-admin whose
roles lock all five server-stored sections (`Profile`, `DisplayAccount`, `Home`, `Playback`, `Subtitles`), so other
clients hide those settings too. The policy writer writes it, checks it at every sync, and puts it back on only where it
turned it off. Each write is audited (`PreferenceAccessMirrored`, `PreferenceAccessRestored`) and recorded in
`PreferenceMirror` **before** the flag is written. The flag has no Dashboard checkbox: read the recovery below before
deleting the plugin with it on.

**Release all locks** (`POST Admin/ReleaseLocks`, the Overview's *Before disabling or deleting Access Roles*): run it
while the plugin is still enabled. On 10.11.5 *Disable* only marks the manifest (the plugin keeps running until the next
restart), uninstalling a disabled plugin doesn't call it, and **10.11.5 deletes a disabled plugin's folder at the next
start** (`PluginManager.DiscoverPlugins`; fixed upstream for 12.0 in `a272efb9a2`): its data and settings stay. Release
all locks turns the mirror's flags back on and, with *Also restore each user's original library access*
(`restoreLibraries=true`), writes each managed user's pre-Access Roles access (the lockout guard applying). Released
users become *Excluded*. A dry run, then `ifPreview`; audited `LocksReleased`. Without it, deleting the folder stops the
locks at the next start, leaving access as last synced and the flags off.

**Uninstalling** an enabled plugin through the Dashboard calls it first: the mirror's flags are written back on (at most
30 s; a failure never stops the uninstall). Library access stays as last synced.

**Recovery after the plugin folder was deleted with the mirror on.** Nothing of the plugin runs any more, so put the flag
back with an API key (Dashboard, *API Keys*). The users the mirror turned off are the keys of `PreferenceMirror` in the
data file (`data\accessroles\accessroles.json`, or after `RESTORE.txt`'s way A
`data\accessroles.removed-<yyyyMMdd-HHmmss>\accessroles.json`). The recovery line is in the user README
([README.md](README.md), "Recovery after the plugin folder was deleted with the mirror on"), kept there in one place
because Repo.Tests reads it from that file: it reads each user's whole policy and posts it back with the flag on, as
UTF-8 bytes with the charset named (Windows PowerShell 5.1 would otherwise send ISO-8859-1 and break non-ASCII tags).
Then delete the API key. Repo.Tests `AccessRolesRecoveryLineTests` runs the line against a loopback stand-in in Windows
PowerShell 5.1 and, where installed, PowerShell 7.

## Export, import and restore

Each write after its own dry run, with a snapshot first ([kit/README.md, "Users and
data"](../../kit/README.md#users-and-data-users-export-and-import-in-storage)).

- **Export** (`GET Admin/Export`): roles, assignments, default roles, known libraries, the ledger, the mirror's records
  and the settings, as the kit's export file (`jellyfin-plugin-export/1`), `accessroles-export-<yyyyMMdd-HHmmss>.json`.
  It holds user and library names.
- **Import** (`POST Admin/Import`, at most 100 MB): `dryRun` (true by default), `mode` `Merge` (the default; a stored
  copy replaced only by a newer one) or `Replace`, `mapUsersBy` `Id` or `Name`, `onUnmanage`, `confirmNoFullAdmin`,
  `ifPreview`. The report counts `Records` per kind and lists what was skipped and why; audited `ImportApplied`.
  - **Users** by id or name; a deleted user is never matched, even by a new user of the same name.
  - **Libraries** by id, then folders, then name; ambiguous or missing ones are left out and listed.
  - **Roles** are checked as the roles API checks a request. What would grant **more** without a missing library (an
    `AllExcept` role, an assignment whose `Remove` names one) is left out whole; an `Only` role or an `Add` loses only the
    missing ones (an `Only` role left with none is left out).
  - **Never brought by a file**: the settings (listed, never applied), the lockout confirmations, and a ledger from
    another server's file (an entry is taken only from this server's own export, for a managed user who has none).
  - **Limits**: over 1,000 roles, 10,000 assignments, ledger entries, mirror records or users, 1,000 libraries or 100,000
    ids: 400 `TooManyRecords`; a newer schema 409; a read-only store 409; over 100 MB 413.
- **Snapshots** (`GET Admin/Snapshots`): the daily ones and those taken before an import, a restore, a deleted user's
  policy or *Remap by name*.
- **Restore** (`POST Admin/Snapshots/Restore?name=`): a `Replace` import of the snapshot, after a snapshot of the state it
  replaces; one at a time (409 `RestoreInProgress`). The locks apply at once; the library sync is **held** until
  confirmed ([the library sync](#the-library-sync)).
- **`POST Admin/Maintenance/Run`** queues the maintenance task.

## Known limitations

- `LibraryHardening` `Strict` isn't done (by-name filtering beyond search, the streams of invisible items); it behaves as
  `Standard`. Anonymous images, static and subtitle streams, collection and playlist names stay open.
- The media's own paths still reach anyone who can see the item, as in stock 10.11.5.
- JE's Hidden Content, Spoiler Guard marks, reviews and bookmarks aren't locked.
- The web guard is cosmetic; native apps were not measured one by one.
- A save without `ifPreview` is accepted unchecked.
- On 10.11.5, *Disable* deletes the plugin's folder at the next start without releasing anything.

## The action manifest

`Jellyfin.Plugin.AccessRoles/Rules/Manifests/jellyfin-api-10.11.5.json` lists **every action of Jellyfin.Api 10.11.5**
(390): its routes and verbs, `[Authorize]` policies, whether it is anonymous, every parameter MVC binds (`parentId`,
`seriesId` and `seasonId` are scope parameters), the rules Access Roles applies (110 actions have one, 112 rules) and a
review note saying why, also for the 280 without a rule. One action per line, sorted, so a Jellyfin update reads as a
diff. It is embedded in the plugin; a 12.x build will carry its own.

- **At runtime** Diagnostics compares it with the actions the server routes: any action missing, unreviewed, changed
  (routes, policies, `[AllowAnonymous]` or parameters) or doubled is listed. A renamed parameter shows as changed: the
  rewrite rules pass through when their argument is missing, so a rename must never be silent.
- **In the tests** `ActionManifestTests` reads a `Jellyfin.Api.dll` and requires exactly the manifest's 390 actions with
  the same shapes. The DLL is only parsed (System.Reflection.Metadata), never loaded; `JellyfinApiMetadataTests` checks
  the reader against MVC's own application model. Other tests pin the rule rows and the arguments each rule reads.
- **Which DLL:** `JELLYFIN_API_DLL` when set, else the installed server's `C:\ProgramData\Jellyfin\Server\Jellyfin.Api.dll`,
  read only. Its informational version must name the manifest's commit (`1e27f460fe741429a1200b3ceb524d34e5d524ce`,
  Jellyfin v10.11.5): another installed version skips the test, another `JELLYFIN_API_DLL` fails it. With `CI` set (to
  anything but `false` or `0`), no DLL fails instead of skipping.
- **For a new Jellyfin version:** point `JELLYFIN_API_DLL` at that version's DLL and write a candidate:

  ```powershell
  $env:ACCESSROLES_MANIFEST_CANDIDATE = "$env:TEMP\candidate.json"
  dotnet test plugins/AccessRoles/tests/Jellyfin.Plugin.AccessRoles.Tests --filter ActionManifestCandidateTests
  ```

  The candidate keeps every known action's rules and review and marks new or changed ones `UNREVIEWED`. Set its header
  (`JellyfinTarget`, `JellyfinVersion`, `Source`), review each marked action against this document's rules, and save it
  as `jellyfin-api-<version>.json`; the tests fail while any action is unreviewed.

### In CI (documented, not built yet)

CI has no Jellyfin install, and Jellyfin.Api isn't on NuGet, so the job builds that one project from a shallow checkout of
the tag and points the test at the result:

```powershell
git clone --depth 1 --branch v10.11.5 https://github.com/jellyfin/jellyfin.git jellyfin-v10.11.5
dotnet build jellyfin-v10.11.5/Jellyfin.Api/Jellyfin.Api.csproj -c Release
$env:JELLYFIN_API_DLL = "$PWD/jellyfin-v10.11.5/Jellyfin.Api/bin/Release/net9.0/Jellyfin.Api.dll"
dotnet test plugins/AccessRoles/tests/Jellyfin.Plugin.AccessRoles.Tests
```

The SDK stamps a build made in a git checkout with `+<commit>`, so it should pass the commit check. UNVERIFIED: this was
not run.

## Building and testing

`dotnet build jellyfin-plugins.slnx` builds it with the rest; the tests are
`plugins/AccessRoles/tests/Jellyfin.Plugin.AccessRoles.Tests` (and Repo.Tests over every plugin). They cover the
resolver's combinations and property tests; the API over HTTP in the kit's harness, with the preview and plan tokens,
export, import and restore; the writer against a double of 10.11.5's user manager (`TestSupport/FakeJellyfinUsers`:
cache and database apart), the library lifecycle and the sync; the settings filter over every manifest action, the
mergers, the counters and `/Me`; every hardening rule through the global filter with negative controls; the action
manifest; and the web (`Web/head.test.mjs`, `Web/accessroles.test.mjs`, `Web/admin.test.mjs`) in the kit's fake DOM.

**In a sandbox**: every probe, its options, what it writes and needs, and the order to run them on one `access` sandbox
start are in [scripts/README.md, "Access Roles checks"](../../scripts/README.md#access-roles-checks). In short:

```powershell
./scripts/dev-server.ps1 -Name access -Port 8103 -Plugins AccessRoles -Community FileTransformation,PluginPages -Background
node scripts/accessroles-probe.mjs --sandbox access --webcheck      # first on a start
# then the other probes in the order scripts/README.md gives
./scripts/Stop-Sandbox.ps1 -Name access
```

`accessroles-roles-probe.mjs` creates its own two synthetic users (`rolesprobe-guest`, `rolesprobe-solo`), pauses the
sync for its run and leaves the store empty again; `accessroles-recovery-probe.mjs --scratch` uses a throwaway
`access-scratch` sandbox for the emergency stop, Degraded mode and the uninstall.
