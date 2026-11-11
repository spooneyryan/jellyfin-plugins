# Kit

The shared, source-only kit. `build/Kit.props` compiles `src/` into each plugin's own assembly, so every plugin
carries its own copy and kit types stay `internal`. `build/TestSupport.props` compiles `testsupport/` into every test
project. `tests/Kit.Tests` compiles both and tests them. `VERSION` is the kit's own version.

## Using it in a plugin

```xml
<!-- Jellyfin.Plugin.<Name>.csproj -->
<Import Project="$(KitRoot)build\Kit.props" />
```

```csharp
internal static class UserRatingsKit
{
    public static KitOptions Options { get; } = new()
    {
        PluginName = "User Ratings",      // build.yaml name
        RoutePrefix = "UserRatings",      // /UserRatings/..., controller class names start with it
        Prefix = "jfur",                  // DOM, CSS and JS prefix
        SchemaPrefix = "UserRatings",     // every schema-visible type starts with it
        DataFolder = "userratings",       // <data>/userratings
        PluginAssembly = typeof(UserRatingsKit).Assembly,
        PluginId = Guid.Parse(Plugin.PluginId),                  // build.yaml's guid; export envelopes carry it
        WebUiEnabled = s => Config(s).EnableWebUi,               // read on every index.html request
        Pages = [new KitPage("toprated", "Top Rated", "star_half", "TopRatedPage") { IsEnabled = s => Config(s).EnableTopRatedPage }],
        PageHostMode = s => Config(s).PageHostMode,              // FallbackOnly (default) | Auto | PluginPagesOnly
        RegisterPluginPages = s => Config(s).RegisterPluginPages,
        ShowFallbackNav = s => Config(s).ShowFallbackNav,
        ReportWebAnchorProblems = s => Config(s).ReportWebAnchorProblems,
        DeletedUserPolicy = s => DeletedUserPolicies.Parse(Config(s).DeletedUserPolicy, DeletedUserPolicy.Delete),
        // Anonymous, server-wide <head> text carrying data-jfur-head; for pages, their fallback-route fragment.
        HeadFragment = context => FallbackRouteHeadFragment.Render(Options!, context.RequestServices)
    };
}

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        if (!KitAbiGuard.Register(serviceCollection, applicationHost, UserRatingsKit.Options)) return; // first statement

        serviceCollection.AddKit(UserRatingsKit.Options);
        serviceCollection.AddIndexHtmlInjection();     // the script tag in index.html
        serviceCollection.AddEmbeddedScript();         // ClientScript: kit.js + Web/js
        serviceCollection.AddKitPages();               // with user pages
        serviceCollection.AddKitDiagnostics();         // ClientHealth and the admin Diagnostics
        serviceCollection.AddKitDiagnosticsSource("Store", s => KitDiagnostics.DescribeStore(/* the JsonDocumentStore */));
        // or, with KitUseSqlite: AddKitSqliteDatabase("userratings.db", RatingsSchema.Migrations, s => Config(s).SnapshotsToKeep);
        serviceCollection.AddKitIdentity();            // item-linked records: MoveJournalReader, IdentityResolver
        serviceCollection.AddKitUsers();               // user-owned records
        serviceCollection.AddSingleton<RatingsUserDataOwner>().AddKitUserDataOwner<RatingsUserDataOwner>();
        serviceCollection.AddKitLibraryChanges();      // library consumers
        serviceCollection.AddSingleton<RatingsLibraryConsumer>().AddKitLibraryConsumer<RatingsLibraryConsumer>();
        serviceCollection.AddHostedService<RatingsWorker>();   // derives from KitBackgroundService
        serviceCollection.AddKitVerifier();            // the verify sweep
        serviceCollection.AddKitDetachedRecords();     // re-resolution, retention archive, reattach, purge
        serviceCollection.AddSingleton<RatingsRecords>().AddKitRecordOwner<RatingsRecords>();
        serviceCollection.AddKitLibraryAccess();       // LibraryAccess and FreshUserData
        serviceCollection.AddKitMaintenance();         // the daily maintenance; kit steps come with the calls above
        serviceCollection.AddKitMaintenanceStep("Snapshots", KitMaintenanceOrder.Snapshots, (s, ct) => /* ... */);
    }
}

// Jellyfin creates these; public, sealed, the service provider only.
public sealed class UserRatingsPostScanTask(IServiceProvider services) : KitPostScanTaskBase(services);
public sealed class UserRatingsMaintenanceTask(IServiceProvider services) : KitMaintenanceTaskBase(services, "User Ratings", new TimeSpan(3, 20, 0));

[ApiController] [Authorize] [Route("UserRatings")] [KitAbiGate] [PluginExceptionFilter]
public class UserRatingsController(IEmbeddedScript scripts) : ControllerBase
{
    [HttpGet("ClientScript")] [AllowAnonymous] public IActionResult GetClientScript() => scripts.ClientScript();
    // With Web/admin/js files: [HttpGet("AdminScript")] [AllowAnonymous] ... => scripts.AdminScript();
}

[ApiController] [Authorize] [Route("UserRatings/Pages")] [KitAbiGate] [PluginExceptionFilter]
public class UserRatingsPagesController(IKitPages pages) : ControllerBase
{
    [HttpGet("{key}")] [Produces("text/html")] public IActionResult GetUserRatingsPage([FromRoute] string key) => pages.Shell(key);
}

[ApiController] [Authorize] [Route("UserRatings/Diagnostics")] [KitAbiGate] [PluginExceptionFilter]
public class UserRatingsDiagnosticsController(IKitDiagnostics diagnostics) : ControllerBase
{
    [HttpPost("ClientHealth")] [RequestSizeLimit(ClientHealthReport.MaxBodyBytes)]
    public Task<IActionResult> PostUserRatingsClientHealth() => diagnostics.ReceiveClientHealthAsync(HttpContext);
}

[ApiController] [Authorize(Policy = Policies.RequiresElevation)] [Route("UserRatings/Admin")] [KitAbiGate] [PluginExceptionFilter]
public class UserRatingsAdminController(IKitDiagnostics diagnostics) : ControllerBase
{
    [HttpGet("Diagnostics")] public ActionResult<IDictionary<string, object?>> GetUserRatingsDiagnostics() => Ok(diagnostics.Describe());
}
```

```
Web/js/00-strings.js 10-selectors.js ... 90-main.js   the ClientScript, in name order, after kit.js
Web/css/*.css                                         appended to KIT_CSS
Web/admin/js/*.js, Web/admin/css/*.css                the optional AdminScript module (declares mount)
Web/anchors.json                                      the jellyfin-web anchors the scripts use
```

**Public types.** Jellyfin and ASP.NET Core create controllers, tasks and the plugin through public constructors, so
only these are public: controllers, the parts Jellyfin creates, the configuration, the service interfaces their
constructors take, DTOs (in `<Root>.Api.Models`; enums anywhere) and Integration facades. Records, stores and service
implementations are internal; service interfaces use only DTOs and other assemblies' types. Each plugin's tests run
`PublicBoundaryChecks.Check` and pin `PublicBoundaryChecks.Describe` ("Test support").

## Server side (`src/`)

| Component | What it does |
| --- | --- |
| `Api/PluginException`, `PluginErrorKind` | Expected failures with a user-facing message: Invalid 400, Forbidden 403, FeatureDisabled 403 (`type` `urn:jellyfin-plugin-kit:feature-disabled`), Locked 403 (`urn:jellyfin-plugin-kit:locked`), NotFound 404, Conflict 409, PayloadTooLarge 413, Unavailable 503. |
| `Api/PluginExceptionFilterAttribute` | Turns a `PluginException` into ProblemDetails (`{title, status, detail}`, `type` for the special 403s; `NonAdminMessage` for callers without the administrator role, wording only) and a Debug line. A body over `[RequestSizeLimit]` becomes 413 "The request is too large." instead of Jellyfin's 500. Other exceptions pass through. `KitProblemDetails`/`KitProblemResult` give the same body without throwing. |
| `Api/CurrentUser` | `GetUserId()`, `IsApiKey()`, `RequireUser(userManager)` (403 for API keys and unknown users), `IsElevatedAsync(HttpContext)` (Jellyfin's `RequiresElevation` policy, **the only admin check for decisions**), `IsAdmin(user)` (display only). |
| `Api/KitAbiGateAttribute` | On every controller: 503 (`abi: {Target, Server, Active}`) for an inert build. |
| `Api/StrictEnumQueryAttribute` | 400 for an enum query value (parameter or `[FromQuery]` model property, under every name MVC binds) that names no member and isn't one of its numbers; Jellyfin 10.11.5's nullable-enum binder would silently use the default. `NamesOnly = true` (User Ratings) refuses numbers. |
| `Api/CommaSeparatedQueryAttribute` | Binds an enum or GUID list comma-separated or repeated; a bad value is a 400 (Jellyfin's own binder drops it). |
| `Hosting/KitOptions`, `AddKit`, `KitPaths` | The identity and setting delegates (above), `CreateLogger` (`Jellyfin.Plugin.<X>.Kit.<Component>`), `Validate()`, `Clock` (the lifecycle's `TimeProvider`; Bookmarks' Debug build moves it by `DeveloperClockOffsetDays`). `KitPaths`: `Root` = `<DataPath>/<DataFolder>`, with `snapshots/` and `archive/`. |
| `Hosting/KitComponent` | One registration of an extension point (record owner, user-data owner, library consumer, maintenance step), created on its own: one that can't be created is left out, logged once, listed as `Unavailable` in Diagnostics and retried at the next use. |
| `Hosting/KitStartupLog` | Logging from `RegisterServices` through Serilog's static logger. |

The other folders are described below: `Web/` (injection, scripts, pages), `Diagnostics/`, `Storage/`, `Sqlite/`,
`Library/`, `Users/` and `Interop/`.

**The ABI guard** (`KitAbiGuard`, `KitAbiStatus`, `KitBuild`). The first statement of every `RegisterServices` compares
the server's version with the compiled target (`KitBuild.JellyfinTarget`, from `Kit.props`'s `KitAbiTarget`, which
follows `JellyfinTarget`): a `10.11` build runs on 10.11.x, a `12` build on 12.x. On a mismatch, or if the check throws,
it logs one Error and registers only `KitAbiStatus`; it never throws. The build is then inert: no `index.html` tag,
every endpoint 503, scheduled tasks and the post-scan task return at once (`KitAbiGuard.IsActive`). Repo.Tests
`KitAdoptionTests` checks every registrator is inert on Jellyfin 12.1 and every controller has `[KitAbiGate]` and
`[PluginExceptionFilter]`. To see it in a 10.11 sandbox:

```powershell
dotnet publish plugins\Bookmarks\Jellyfin.Plugin.Bookmarks -c Debug -o artifacts\deploy\Bookmarks\Debug -p:KitAbiTarget=12
./scripts/dev-server.ps1 -Name default -Background -NoBuild
```

The log shows one `[ERR] Jellyfin.Plugin.Bookmarks.Kit.AbiGuard` line and every `/Bookmarks` endpoint answers 503.
Deploy a normal build afterwards (`./scripts/Deploy-Plugin.ps1 -Plugin Bookmarks -DataDir .sandbox\default\data
-Configuration Debug`).

### index.html injection (`Web/`)

Every plugin adds `<script data-<pfx>-plugin="<version>" src="../<Route>/ClientScript?v=<version>-<mvid8>" defer>` to
jellyfin-web's `index.html` per request, through its own `IndexHtmlInjectionStartupFilter` (no File Transformation).
The relative `src` works under any BaseUrl.

- Only GET `.../web/` and `.../web/index.html` (any case) under the BaseUrl, also behind one `/emby` or `/mediabrowser`
  prefix; everything else passes through unbuffered. `KitOptions.WebUiEnabled` off gives exactly the response without
  the plugin. The kit assumes no position among other plugins' filters and reads no other tag.
- The request loses `If-None-Match`, `If-Modified-Since`, `If-Range`, `Range` and `Accept-Encoding`, so the pipeline
  answers a full 200 (Jellyfin Enhanced removes only the last three).
- A 200 `text/html` without `Content-Encoding`, up to 1 MB, gets the insert-only transform (the tag before the last
  `</body>`, the head fragment before the first `</head>`, each skipped when its marker is there, strict UTF-8), loses
  `ETag`, `Last-Modified` and `Accept-Ranges`, and gets `Cache-Control: no-cache`. The head fragment must carry
  `data-<pfx>-head` and is anonymous content.
- Any failure serves the original bytes with one warning per process (`Jellyfin.Plugin.<X>.Kit.WebInjection`).
- Tests: `tests/Kit.Tests/Web` in `WebHostHarness`, also beside a Jellyfin Enhanced 12.8-like filter in both orders; in a
  sandbox `scripts/index-probe.mjs` and `webchecks/bookmarks-injection*.json`.

### Web client (`web/`, `Web/EmbeddedScript`)

`EmbeddedScript` builds, once per process, from the embedded `web/kit.js`, `web/kit.css` and the plugin's `Web/` files,
served as `text/javascript; charset=utf-8`, `no-cache`, the MVID as `ETag` (503 for a missing resource or non-UTF-8 file):

- **ClientScript**, one IIFE: `KIT_BUILD` (`plugin`, `prefix`, `route`, `version`, `kit`, `mvid`), the double-load guard
  `window.__jfkit[prefix]`, `KIT_CSS` (kit.css with `__PFX__` replaced, then `Web/css`), `KIT_ANCHOR_DECLARATIONS`,
  `kit.js`, `var kit = createKit(...)`, then `Web/js` in name order, sharing the scope.
- **AdminScript** (only with `Web/admin/js`): an ES module over `Web/admin/css` that exports the admin files' `mount`; the
  dashboard page's stub does `import(ApiClient.getUrl('<Route>/AdminScript')).then(m => m.mount(view, params))`.

`kit.js` is plain ES2019. The `kit` object:

| Module | What it does |
| --- | --- |
| `start()` | Injects `KIT_CSS`, starts the observer, waits for `window.ApiClient` (never touched synchronously) and the user; `kit.timing` records the milestones. |
| `h`, `append`, `icon`, `iconButton`, `button`, `input`, `textarea`, `select`, `field`, `setHidden`, `upgrade`, `makeFocusable` | DOM builders that write text nodes only, with the native classes (`button-submit`, `emby-input`, `emby-textarea`, `emby-select-withcolor`) for theming; `makeFocusable` adds jellyfin-web's `focusable` so the TV D-pad reaches our own controls. |
| `osdButton`, `detailButton`, `headerButton`, `section`, `card`, `listItem`, `list`, `grid`, `itemHref`, `imageUrl` | Nodes shaped like jellyfin-web's own; cards and rows carry `data-id` (N format) and `data-type` like native cards (so Jellyfin Enhanced's Hidden Content and other plugins treat them alike), link to `#/details?id=…` (`href: false` for an item the viewer can't open) and lazy-load images. |
| `searchBox`, `pager`, `notice` | A debounced search field (300 ms; Enter and clear at once), a "Load more" pager pressed by an `IntersectionObserver` (not on the TV layout), and a one-sentence notice (`role="alert"` for errors). |
| `toast`, `dialog`, `confirm` | Toasts in a polite live region with button or link actions. `confirm({ title, message, note, confirmLabel, cancelLabel })`, `note` a quieter second line. Our own modal dialog: `role="dialog"`, `aria-modal`, Escape closes, Tab stays inside, initial focus inside, focus returns (or to `returnFocus`), keys never reach the player. `dialog({ scopedKeys: true, onKey })` (every Bookmarks dialog) adds a `window` capture listener for keys inside the panel, so Jellyfin Enhanced's capture listener never sees them; a TV remote's or gamepad's Back closes it (Backspace too on the TV layout or a Hisense VIDAA TV, never in an editable field); on the TV layout the D-pad moves through the panel. IME compositions are left alone. |
| `api(method, path, opts)` | `fetch` with the `MediaBrowser … Token=` header, JSON (or `text`/`blob` bodies sent as they are), a 30 s timeout, ProblemDetails as `KitApiError { status, title, detail, type, errors, code, problem, isFeatureDisabled, isLocked }`; a 401 raises `<prefix>:session-expired`; nothing is sent before the user is known. |
| `user`, `observe`, `rescan`, `onView`, `onNavigate` | The current user (`onChange` for sign-in without reload; `isAdmin()` cosmetic); one throttled body `MutationObserver` per plugin plus `viewshow`/`hashchange`, each scanner in its own try/catch. |
| `playback` | `onReport`, `sessionId()`, `seekTo`, `pause`, `unpause`, `playItem` through the Sessions API (sent again if playback doesn't start). |
| `format`, `storage`, `layout`, `je`, `t`, `hashParams`, `env.has`, `report` | Formatting (`h:mm:ss[.t]`, sizes, dates, stars, `ago`); `localStorage` under `<prefix>:`; `isTv()`, `isMobile()`, `isExperimental()`; `je.present()`, `je.reviews(scope)` and `je.tvSectionJump(...)` (on the TV layout, a jump past Jellyfin Enhanced 12.14's closed Reviews section, whose hidden controls would otherwise take the D-pad); strings; feature detection. |
| `anchors`, `health`, `pages`, `nav` | "Coupling policy and health" and "Page hosting" below. |
| `shortcuts` | `normalize` (to `Shift+Meta+Ctrl+Alt+Key`), `comboOf`, `reserved`, `isBackKey`, `arrowOf`, and `bind(combo, handler, { when })`: one `document` **bubble-phase** listener per kit; skips handled events, repeats, keys typed into fields and keys while a dialog is open ("Jellyfin Enhanced interop"). |
| `actionSheet` | `addItem({ id, icon, text, when, onSelect })`: an **admin-only** row in jellyfin-web's item "⋮" menu (cards, rows, details page's More) or, with `when`, the selection bar's bulk menu; `onSelect(ctx)` after the sheet closes, `ctx` = `{ itemIds, surface, isBulk, itemType, isFolder, serverId, commands, exitSelection, trigger, … }`. Trigger listeners are read-only. |
| `nativeDialog(opts)` | A jellyfin-web `dialogHelper` form dialog, history-enabled (Back closes it), for action-sheet flows; `trapFocus`, `returnFocus`, `escapeClearsText` opt-in. |

**Coupling policy.** jellyfin-web names appear in kit.js only in `KIT_NATIVE_CLASSES` and `KIT_ANCHORS`. A plugin
declares its jellyfin-web selectors only in `Web/anchors.json`, reaches them by name from `Web/js/10-selectors.js`, and
writes no jellyfin-web selector or native class itself; `testsupport/Web/WebCouplingChecks` enforces it.

**Theming.** kit.css styles only its own `__PFX__-` classes: neutral surfaces, `var(--jf-palette-divider, …)`, the accent
`var(--jf-palette-primary-main, #00a4dc)` as `--<prefix>-accent` beside `-danger`, `-warning`, `-success`; opaque
surfaces come from native classes, so every theme applies. Never `--jf-palette-background-*`, `--jf-palette-text-*` or
`--jf-palette-primary-contrastText`. Form fields are `max-width: 100%` (a phone at 375 px).

**Tests.** `node --test kit/web/tests/*.test.mjs` (Node 22) on `fake-dom.mjs`, a small fake page with a manual clock and a
fake `ApiClient`: `kit`, `pages`, `health`, `shortcuts`, `actionsheet` (jellyfin-web's action sheet played as its
`actionSheet.ts` behaves), and `client-script`/`admin-script` for any plugin's built scripts. Kit.Tests runs them (skipped
without Node). A plugin's tests call `KitScriptChecks.CheckSyntaxAsync`, `RunClientScriptTestsAsync` (and
`RunAdminScriptTestsAsync`), `WebCouplingChecks.CheckPluginScripts`, `CheckPluginStyles`, `CheckAnchors` and
`DashboardPageChecks.Check` (Repo.Tests `WebConventionCoverageTests`).

### Page hosting (`Web/Pages/`, `kit.pages`, `kit.nav`)

A plugin lists its user pages in `KitOptions.Pages` and registers the same keys with `kit.pages.register`. Two hosts:

- **Fallback route** `#/<pfx>-<key>` (default, `PageHostMode=FallbackOnly`): jellyfin-web shows its FallbackRoute page
  `#fallbackPage` and the kit renders into it. The head fragment hides "Page not found" and sets the title while
  `index.html` is parsed.
- **Plugin Pages** `#/userpluginsettings.html?pageUrl=/<Route>/Pages/<key>` (`Auto`/`PluginPagesOnly`, for the pages
  Plugin Pages lists): its host inserts the server's shell and the kit mounts it.

Server:

- `KitPage(key, title, icon, idSuffix) { IsEnabled }`; the Plugin Pages Id is `Jellyfin.Plugin.<Route>.<IdSuffix>`.
  `IsEnabled` is read per request (once at startup for Plugin Pages). `KitOptions.Validate()` checks the lot.
- `IKitPages.Shell(key)` for `GET /<Route>/Pages/{key}` (`[Authorize]`, `text/html`, `no-cache`; name the action
  `Get<Route>Page`): an enabled page gives its page root with a note shown only when our script doesn't run; a page
  turned off gives 200 with "<Title> is turned off by your server admin." and no page marker; other keys 404.
- `PluginPagesRegistrationService` (with `RegisterPluginPages` in `Auto`/`PluginPagesOnly`) registers enabled pages
  through `PluginPagesBridge`: Plugin Pages 3.x `RegisterPage`, else 2.4.x's DI `IPluginPagesManager`; it never writes
  Plugin Pages' `config.json`, never throws, and 2.4.x changes need a restart. Diagnostics `Pages` shows each host.
- `FallbackRouteHeadFragment.Render` writes the pre-hide CSS, the route class, a capture-phase `viewbeforeshow` titler
  and a JSON block with the keys, those turned off, `PageHostMode` and `ShowFallbackNav`: server-wide settings only.

Client:

- `register({ key, title, icon, idSuffix, order, render(body, params, context) })`: `render` gets an emptied body on
  every show; `context.isShown()` turns false when the host hides the page, so a poll stops. `href`, `link`, `host`,
  `params`, `state`; `announced(key)` (the head fragment lists it) and `enabled(key)` (listed and on), for an entry
  point outside the nav, such as a header button.
- **State in the hash.** Filters, sort and paging live in the hash parameters, so a reload, a deep link and Back show
  the same list. `setParams(key, params, { replace })` changes them without a render.
- **Fallback routes.** On a route change inside `#fallbackPage` the kit swaps its root and dispatches `viewbeforeshow`,
  `pagebeforeshow`, `viewshow` and `pageshow` as React's `Page` does (else `appRouter.show()` hangs). Leaving for an
  unknown route hands the page and its title back after about 1 s. The host asks the shell first: no marker means off.
- **Hosts.** `FallbackOnly` makes no request; `Auto`/`PluginPagesOnly` ask `GET /PluginPages/User` once per page load.
  Plugin Pages adds its tag a few seconds after a server start, so a page loaded then uses the fallback routes.
- **"Library tools"** (`kit.nav`): a shared drawer section (`.jfkit-nav[data-jfkit-nav]`) before `.userMenuOptions`; each
  plugin adds `navMenuOption` links (`data-jfkit-plugin`, `data-order`) with no click handler, re-added after drawer
  rebuilds. No link for pages turned off or with `ShowFallbackNav` off. A page Plugin Pages lists gets our link in
  `Auto` only when the drawer lacks Plugin Pages' own link about 1 s after it was built, in `PluginPagesOnly` never.
- **Layouts** (`scripts/webchecks/layouts.json`): TV and mobile use the same drawer. **The experimental layout shows no
  "Library tools"** (its app bar replaces the drawer); its pages are reached by deep link only. Accepted for 10.11.
- **DOM contract between our plugins:** the nav section and its links, and `data-jfkit-claim="<pfx>"` plus
  `data-jfkit-orig-title` on `#fallbackPage`.

Tests: `web/tests/pages.test.mjs`; Kit.Tests `Web/Pages` (the bridge against fake Plugin Pages 2.4.x and 3.x assemblies);
in a sandbox `scripts/webchecks/pages-*.json` on Bookmarks' developer page demo.

### Coupling policy and health (`Diagnostics/`, `kit.anchors`, `kit.health`)

jellyfin-web changes its DOM between releases; a plugin must notice, switch the affected feature off without breaking
the page, and tell the admin.

```json
{ "comment": "...", "anchors": [
  { "name": "detailButtons", "css": [".mainDetailButtons"], "required": true, "views": ["details"],
    "note": "The button row inside detailPage (itemDetails-index-html chunk). Missing: no Add bookmark button." } ] }
```

- **Anchor lists**: the kit's `web/anchors.json` (`kit.<name>`, kept equal to `KIT_ANCHORS` by `health.test.mjs`) and the
  plugin's `Web/anchors.json`. `css` is the primary selector then fallbacks. `WebAnchorCatalog` rejects a bad entry into
  `Problems`, which fails the plugin's tests: a unique camelCase name up to 40 characters, 1–8 safe selectors, 1–8 views,
  a note up to 400 characters citing its source and the effect of a miss. `scripts/Test-WebAnchors.ps1` looks for the
  anchors in an installed web client.
- **Browser**: `kit.anchors.find(name, scope)` (primary, then fallbacks, else `null`: the feature stays off).
  `kit.health.expect(entries, view)` on `viewshow`: an anchor still missing after 4 s is warned once per page load and
  reported 2 s later, at most once per browser every 10 minutes: `POST /<Route>/Diagnostics/ClientHealth` with
  `{WebVersion, Client, ClientVersion, Layout, Experimental, PluginVersion, Missing:[{Anchor, View, Count}]}`.
- **Server**: nothing is trusted, since any signed-in user can post. API keys 403, over 16 KB 413, `ReportWebAnchorProblems`
  off drops it; otherwise always 204. Every field is validated against fixed ASCII patterns (versions, client names,
  `Layout` `Desktop`/`Mobile`/`Tv`), `Missing` 1 to 32 declared anchor/view pairs with counts 1–1000; invalid reports are
  dropped (Debug). One accepted report per user per 10 minutes, at most four web clients per user; one Warning per anchor
  and web client (de-duplication capped at 200); at most 200 entries in memory, the oldest-updated making room, so
  forged reports can't hide a real problem.
- **Diagnostics** (`GET /<Route>/Admin/Diagnostics`, admins only, `KitDiagnostics.Describe`): `PluginVersion`,
  `KitVersion`, `Abi`, the plugin's sources (`Store` via `DescribeStore`, `Users`, the other kit entries below), `Pages`,
  `WebAnchors` (counts per outcome) and `ClientHealth` (per anchor, view and client). Plain values; the settings page
  renders them with `textContent` only.
- **Simulating a miss**: publish with `-p:KitRenameAnchors=<name>[,<name>]` (renames those anchors' classes and ids in the
  scripts; logged), install with `Deploy-Plugin.ps1 -NoBuild` in a sandbox, never on a server in use.

Tests: Kit.Tests `Diagnostics/` (forged and flooding reports, both endpoints), `web/tests/health.test.mjs`; in a sandbox
`scripts/webchecks/health-*.json`.

### JSON stores (`Storage/`)

```csharp
internal sealed record UserRatingsDocument : IVersionedDocument   // a record: a change is a `with` copy
{
    public const int CurrentVersion = 2;
    public int Version { get; init; } = CurrentVersion;
    public IReadOnlyList<UserRatingsRecord> Records { get; init; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }   // unknown fields survive
}

var store = new JsonDocumentStore<UserRatingsDocument>(Path.Combine(kitPaths.Root, "ratings.json"),
    UserRatingsDocument.CurrentVersion, () => new UserRatingsDocument(), [new RatingsMigration1To2()], logger);
var doc = store.Current;                                                 // never blocks
await store.UpdateAsync(d => d with { Records = [.. d.Records, r] }, ct); // one writer
await store.SnapshotAsync(Path.Combine(kitPaths.Root, "snapshots"), keep: 7, ct);
```

One file per store, the document in memory as an immutable snapshot (lock-free reads), loaded lazily. Format
(`JsonStoreFormat`): PascalCase, indented, enums as strings, nulls omitted, UTF-8 without BOM. `Version` is the schema
version (1 when missing); one `IJsonMigration` per version below the current one, on the parsed `JsonObject`. **The
rule: the store never starts empty on its own where data may be lost.**

- **Write**: `<file>.tmp`, flushed, `File.Replace` over the file keeping `<file>.bak`, then published. A failure changes
  nothing and throws 503.
- **Load** (the table below has every case): a file that reads is the data. An empty file (no bytes or white space) is
  moved to `<file>.corrupt-<yyyyMMddHHmmss>` and counts as missing. An unreadable one is replaced by a readable `.bak`,
  else the newest snapshot that holds data, and is moved aside only as part of that step. A locked file, or a snapshot in
  use, is 503 and retried at the next use; a move that fails is moved back (503 `MoveAsideFailed`/`UpgradeCopyFailed`).
- **Restore at load**: snapshots (daily and pre-operation, in `SnapshotDirectory`) newest first, but after a restore
  (`<file>.last-restore`) the ones taken since first; those dated more than a day ahead last. The chosen one is upgraded,
  passed to the plugin's `afterRestoreAtLoad` (Bookmarks ends its clip jobs) and saved: `RestoredFromSnapshot`. When
  none can be used the store is **read-only** (writes 503, no snapshots, `ExportData` 503, admin operations 409 except the
  recovery): `ReadOnlyDataMissing`, or `ReadOnlyDataUnreadable` with the unreadable file left in place.
- **Recovery** (`RecoverAsync(directory, snapshotName | null, startEmpty)`): the admin's only way out of those two states
  and the only way to start empty: a listed snapshot (checked; 404/409) or the empty document. The file is read again
  first: one that reads now is 409 `ReadableFileInPlace` ("restart the server to use it"). What is in place is moved
  aside as `.corrupt-<ts>`, the result is writable at once; `WaitUntilWritableAsync` releases background writers.
  `recoveryPage: false` words the banner for a fix with the server stopped instead.
- **Versions**: newer → `ReadOnlyNewerVersion` (served, never touched, writes 503, exported as it is). Older → the bytes
  are copied to `<file>.v<N>-<ts>` first, migrated and saved (`Migrated`). A failed migration →
  `ReadOnlyMigrationFailed`: the file stays byte-identical, an empty stand-in is served read-only, export 503, one Error
  names the step (`ThrowIfReadOnly()` keeps that stand-in from answering 404). Non-admins get
  `StoreMessages.ReadOnlyForUsers` wording.
- **Snapshots**: `snapshots/<store>-<yyyyMMdd-HHmmss>.json` (`SnapshotAsync`, keep N), and pre-operation
  `snapshots/pre-op/<store>-<operation>-<ts>.json` (`PreOpSnapshotAsync`; `restore`, `import`, `remap`, `apply-policy`,
  `user-deleted`, `purge`, `other`, `migrate`, `moderate`; the newest 5). Future-dated ones are never rotated.
- **Kept snapshots**: after an incident (started empty, restored, or a `.bak` older than a snapshot holding other data)
  every snapshot there is then is recorded **by name** in `<file>.keep-snapshots` (`{KeepSnapshotsBeforeUtc, SinceUtc,
  Reason, Incident, Snapshots}`) and never rotated, across restarts, until one of them is restored or an admin deletes
  the marker. A marker of an earlier format (a time alone) is converted once.
- **Restore snapshot** (`RestoreSnapshotAsync(directory, name)`): a listed name, checked, upgraded; a `restore`
  pre-operation snapshot first (sparing the source). `ListSnapshots` gives `{Name, Kind, Operation, Utc, Size,
  SchemaVersion, Kept}`. The plugin runs its verify sweep after a restore or recovery. `ReadSnapshotAsync(directory,
  name)` reads a snapshot's document as a restore would (checked, upgraded) without restoring it, for a dry run.
- **Export**: `ExportData()` → `StoreExportData` (data plus schema version) for `ExportEnvelope.Create`; `ReadData` reads it
  back (older migrated, newer 409).
- **Kept files** (`ListKeptFiles()`): the `.v<N>-*` copies (the file to put back for the previous plugin version) and
  `.corrupt-*` files, kept until an admin deletes them; the status stays until the plugin restarts.
- **Size**: a 5 MB document loads in about 0.1 s and rewrites in about 35 ms; above about 5 MB, use SQLite.

Every load, by what is found ("usable" snapshots: one reads and holds data; "unusable": some may hold data but none can
be used; "in use": the one to try can't be opened just then):

| The file | Its `.bak` | Snapshots | Outcome |
|---|---|---|---|
| reads | anything | anything | `Ok`, `Migrated`, `ReadOnlyNewerVersion` or `ReadOnlyMigrationFailed`; the `.bak` isn't read |
| missing, empty or unreadable | reads | anything | `RecoveredFromBackup` (or the `.bak`'s own version outcome); an unreadable file moved aside first; the snapshots kept and the newest named when it was taken after the `.bak` and holds other data |
| unreadable | reads, needs an upgrade | anything | when its copy from before the upgrade can't be written: 503 `UpgradeCopyFailed`, the file moved back, tried again at the next use |
| missing | missing | none | `Ok`: a fresh install |
| missing | empty | none | `RecoveredEmpty`, naming the `.bak` moved aside |
| empty | missing or empty | none | `RecoveredEmpty`, naming what was moved aside |
| missing or empty | missing or empty | usable | `RestoredFromSnapshot` |
| missing or empty | missing or empty | unusable | `ReadOnlyDataMissing` |
| unreadable | missing, empty or unreadable | usable | `RestoredFromSnapshot`, the unreadable file and `.bak` moved aside first |
| missing or empty | unreadable | usable | `RestoredFromSnapshot`, the unreadable `.bak` moved aside first |
| unreadable | missing, empty or unreadable | none or unusable | `ReadOnlyDataUnreadable`, what couldn't be read left in place |
| missing or empty | unreadable | none or unusable | `ReadOnlyDataUnreadable`, the `.bak` left in place |
| missing, empty or unreadable | missing, empty or unreadable | in use | 503, nothing unreadable moved, tried again at the next use |
| locked | | | 503, nothing moved, tried again at the next use |
| missing, empty or unreadable | locked | | 503, tried again at the next use |
| unreadable | unreadable, can't be moved | usable | 503 `MoveAsideFailed`, the file moved back, tried again at the next use |

Every plugin with a JSON store runs the conformance suite on it (Repo.Tests checks each `JsonDocumentStore<T>` has one):

```csharp
public sealed class UserRatingsStoreConformanceTests : StoreConformanceTests
{
    private protected override StoreConformanceSubject CreateSubject() => new UserRatingsStoreConformance();
}

internal sealed class UserRatingsStoreConformance : StoreConformance<UserRatingsDocument>
{
    public override int CurrentVersion => UserRatingsDocument.CurrentVersion;
    public override IReadOnlyList<IJsonMigration> Migrations => [new RatingsMigration1To2()];
    public override IReadOnlyList<StoreFixture> Fixtures => [/* files from earlier releases and older versions */];
    public override UserRatingsDocument CreateEmpty() => new();
    public override UserRatingsDocument AddRecord(UserRatingsDocument d, int n) => d with { Records = [.. d.Records, Record(n)] };
    public override int CountRecords(UserRatingsDocument d) => d.Records.Count;
}
```

### SQLite (`Sqlite/`, `KitUseSqlite`)

For data that needs server-side queries, many small writes or unbounded tables (User Ratings' `userratings.db`).
Compiled only with `<KitUseSqlite>true</KitUseSqlite>` before the import of `Kit.props` (which then references
Microsoft.Data.Sqlite.Core 9.0.11 with `ExcludeAssets=runtime`: the server's own copy, none shipped) or `TestSupport.props`
(the full package, whose native SQLite is the server's own build, in the test output only).

```csharp
internal static class RatingsSchema   // one SQL step per version from 1; user_version after the step
{
    public static IReadOnlyList<SqliteMigration> Migrations { get; } =
    [
        new(1, "M001_Initial", "CREATE TABLE Ratings (...); CREATE TABLE RatingKeys (...REFERENCES Ratings(Id) ON DELETE CASCADE...);"),
        new(2, "rebuild Ratings", "CREATE TABLE new_Ratings (...); INSERT INTO new_Ratings SELECT ...; DROP TABLE Ratings; ALTER TABLE new_Ratings RENAME TO Ratings; CREATE INDEX ...;") { RequiresRebuild = true },
    ];
}

// The database, its hosted service (opened after the startup gate), Diagnostics "Store", and the maintenance steps
// IntegrityCheck (first), Snapshots and Checkpoint (last).
serviceCollection.AddKitSqliteDatabase("userratings.db", RatingsSchema.Migrations, s => Config(s).SnapshotsToKeep);

var summary = database.Read(c => QuerySummary(c, itemIds));                  // pooled connection
await database.WriteAsync((c, t) => UpsertRating(c, t, row), ct);            // one writer, BEGIN IMMEDIATE
var path = await database.PreOpSnapshotAsync(SnapshotOperations.Purge, ct);  // before a risky operation
var data = database.ExportData((c, t) => ReadExportData(c, t));              // one read transaction
```

The JSON store's rules hold (never an empty start where data may be lost, the same read-only states, kept snapshots by
name, `ReadableFileInPlace`, the restore order):

- **Open.** Every connection runs `journal_mode=WAL; synchronous=NORMAL; foreign_keys=ON; busy_timeout=5000;
  journal_size_limit=4194304; secure_delete=ON`. Order: `user_version` from the header and through the `-wal` (a newer
  version's is `ReadOnlyNewerVersion`, the file untouched), `quick_check`, the upgrade. A file that can't be opened is
  `Unavailable` (503, retried after 5 s). Leftover `.tmp` files are deleted, stray `-wal`/`-shm` quarantined, and an
  interrupted recovery resumed (below).
- **Deleted data doesn't linger.** `secure_delete=ON` zeroes what a write deletes (opt out: `secureDelete: false`); a
  migration step with `VacuumAfter` vacuums once; `RequestCheckpoint()` truncates the `-wal` in the background after a
  private deletion (retried for about two minutes, then by the next commit and the daily `Checkpoint` step).
- **Transient errors are never damage.** A page that can't be read (a lock by a backup tool) leaves the database
  `Unavailable` and retried; damage found at open is confirmed by `integrity_check` 2 s later on a fresh connection. A
  snapshot that can't be read just then stops a restore ("try again later"); one the server may not read is passed over.
- **Busy.** Locks answer 503 `Busy` within about 5 s (the command timeout and the writer-lock wait share that budget);
  never damage. Admin restores wait without a limit.
- **Migrations**: all pending steps and `user_version` in one transaction; a failure rolls back (`ReadOnlyMigrationFailed`,
  the file unchanged). A `migrate` pre-operation snapshot first when the database has data. **`RequiresRebuild`**:
  `foreign_keys=OFF` before `BEGIN` (checked), sqlite.org's 12-step procedure (views naming the table dropped and
  recreated in the step), `foreign_key_check` must pass.
- **Damage** (`SQLITE_CORRUPT`, `SQLITE_NOTADB`, or a failed check): `ReadOnlyCorrupt`, undamaged tables readable; a
  held read-only connection stops any checkpoint into the damaged file. Each pre-operation snapshot is preceded by an
  `integrity_check` of the live database: damaged, the operation is refused (409 code `Damaged`, writes 503).
- **Corruption restore** (automatic at open; at run time the admin's `RecoverAsync(null)` or `RestoreSnapshotAsync`):
  the newest snapshot that passes `integrity_check` is copied in; the database and its `-wal`, `-shm` and `-journal` move
  **together** into `quarantine-<yyyyMMdd-HHmmss>/` with a `reason.txt`. A failure after that **puts everything back**
  (`RecoveryPutBack`; else `RecoveryNotPutBack` names the folder and nothing is written). **No snapshot passes → nothing
  moves**; starting empty is only the admin's `RecoverAsync(null, startEmpty: true)`. A good database back in place is
  never quarantined (checked as a copy first). A missing or empty database beside snapshots of data is restored the
  same way, never silently recreated. Pools are cleared per database (`ClearPool`), never `ClearAllPools`.
- **Recovery journal** `<db>.recovery` (written through a flushed `.tmp` before anything moves, with the placed file's size
  and SHA-256): at open it puts back what an interrupted recovery moved; what it placed is deleted only if it is that file
  byte for byte. Otherwise the database stays read-only, never replaced by an empty one. It may name only the
  database's own files and a `quarantine-*` folder beside it. A database put back by hand is used, the folder named
  (`KeptAside`) at every open.
- **Snapshots** `snapshots/<db>-<ts>.db` and `snapshots/pre-op/<db>-<operation>-<ts>.db` (the newest 5): `BackupDatabase`
  to `.tmp`, rollback journal mode, `integrity_check`, rename; a failing snapshot is deleted. Opt-in **by age** (User
  Ratings: `preOpKeepWithin: TimeSpan.FromDays(7)`, `preOpCopies: ["userratings-archived.jsonl"]`): every pre-operation
  snapshot within that time, up to 50, and **none older**, so a copy holding a deleted user's data doesn't outlive it.
- **Restore snapshot** (`RestoreSnapshotAsync(name)`): a listed snapshot that passes its check, a `restore` snapshot first,
  then `BackupDatabase` into the live database under the writer lock (lock order: writer, then snapshot). With the server
  stopped: delete `-wal` and `-shm` and copy the snapshot over the database.
- **Import**: `SqliteStoreImport.RunAsync` is `JsonStoreImport`'s twin; the plugin's `SqliteImportAdapter.Apply` runs in the
  writer's transaction after an `import` snapshot, and a dry run is the same `Apply` rolled back.
- **Diagnostics `Store`** adds `Engine`, `Generation` (moves on every commit, restore and recovery, for caches) and
  `RestoreCount`, `LastQuickCheck`, snapshot counts, `Recovery`, `Checkpoint`, `Notice` and `Quarantines` (the newest 20,
  kept until an admin deletes them).
- **Isolation.** A Microsoft.Data.Sqlite the server can't bind fails where the JIT compiles a method naming its types,
  and a DI-built constructor doing so stops the whole server. So no SQLite type appears in a constructor, field, DI-built
  service signature or **controller action** (signature or body); SQLite work sits in `NoInlining` methods inside a
  catch, and a binding failure is `Unavailable` (`SqliteHostingTests` checks the IL).
- **Never** call `ClearAllPools` (Jellyfin's `jellyfin.db` shares the process), ship a SQLite DLL (Deploy refuses
  `Microsoft.Data.Sqlite*`, `SQLitePCLRaw*`, `e_sqlite3`), reference a version newer than the server ships (9.0.11 on
  10.11.5; `ServerPackagePinTests`) or call `SQLitePCL.raw`. `Release` (at stop) checkpoints and leaves only `<db>.db`.

Tests: `kit/tests/Kit.Tests/Sqlite` on real files, with `RecoveryStepFault` (tests only) failing each recovery step, and
the recovery, busy, clock, hardening and journal suites.

### Users and data (`Users/`, export and import in `Storage/`)

A plugin with user-owned records registers one `IUserDataOwner` per store (`AddKitUserDataOwner<T>()`) and calls
`AddKitUsers()`. The kit applies `DeletedUserPolicy` when Jellyfin deletes a user, reconciles daily (the `Users`
maintenance step) and provides the admin actions.

```csharp
internal sealed class RatingsUserDataOwner : IUserDataOwner
{
    public string Name => "userratings";
    public bool IsReadOnly => _store.IsReadOnly;
    public KnownUsers KnownUsers => _store.Current.KnownUsers;       // user id (N) -> last seen name
    public IReadOnlyDictionary<Guid, int> CountRecordsByUser() => ...;
    public Task RefreshKnownUsersAsync(IReadOnlyDictionary<Guid, string> names, CancellationToken ct) => ...;
    public Task<string?> SnapshotBeforeAsync(string op, CancellationToken ct) => ...;   // a pre-operation snapshot
    public Task<int> HandleUserDeletedAsync(Guid userId, DeletedUserPolicy policy, CancellationToken ct) => ...;
    public Task<int> RemapUsersAsync(IReadOnlyDictionary<Guid, Guid> map, CancellationToken ct) => ...;
}
```

- **`KitUsers.GetAll(IUserManager)`** is the only way to list users: 10.11.5's `Users` property became `GetUsers()` in
  10.11.9, so a direct read throws on the other (`KitUsersCoverageTests` checks sources and assemblies).
- **Only an explicit deletion applies the policy.** `KitUserDeletedConsumer` (`UserDeletedEventArgs`) has each owner
  take a `user-deleted` snapshot (when `WouldChangeRecords`) and apply the policy (`KeepAsOrphan` if the setting can't be
  read). An owner that fails is marked `PolicyPending` and retried by each reconciliation, **only while the user is still
  gone** and the user list can be read (fail closed).
- **The reconciler never changes a record.** It marks unknown user ids as orphans (hidden from views and aggregates via
  `IsOrphan`, listed in Diagnostics) and refreshes `KnownUsers`. **Guard:** when more than one id, or over 10 % of the
  records, would become orphans in one run, it changes nothing and says "Stored users don't match this server; was data
  restored from another server? …".
- **Marks** live in `kit-users.json` (a kit JSON store). Users deleted here (`Deleted`, `PolicyApplied`) stay marked while
  the server doesn't know their id, so their records are never given to anyone by name. If the file can't be read,
  *Remap by name* and imports by name answer 409 (fail closed); `StartMarksEmptyAsync()` is the admin's way out
  (`KitOptions.UsersRecoveryPage`).
- **`RemapByNameAsync(dryRun)`** (`POST /<Route>/Admin/Users/Remap?by=Name`): an unknown id whose last seen name is one
  server user's goes to that user, after a `remap` snapshot. Refused with a reason: `NoKnownName`, `NoUserWithThisName`,
  `SameNameTwice`, `DeletedOnThisServer`. With `ifPlan` (User Ratings) the real run needs the dry run's `PlanToken` and
  refuses a changed plan (409).
- **`ApplyPolicyAsync(userIds)`** (`POST /<Route>/Admin/Users/ApplyPolicy`): 1 to 1000 ids, no current user (400), no
  read-only owner (409), 503 when the user list can't be read; snapshot, policy, mark, reconcile.
- An owner that can't be created is skipped (the admin actions 409); Diagnostics `Users` lists orphans, pending
  policies and the marks file's state.

```csharp
var envelope = ExportEnvelope.Create(options, store.ExportData(), ExportUser.For(storedIds, doc.KnownUsers, serverUsers,
    reconciler.DeletedUserIds), settings: config, archive: archivedRecords, serverId: host.SystemId);
return envelope.ToFileResult(HttpContext, "userratings");   // <stem>-export-<ts>.json, Cache-Control: no-store

// [HttpPost("Import")] [RequestSizeLimit(ExportEnvelope.MaxImportBytes)]; read from Request.Body, never IFormFile
var request = ImportRequest.Parse(mode, dryRun, mapUsersBy);   // defaults: Merge, a dry run, Id
var report = await JsonStoreImport.RunAsync(store, await ExportEnvelope.ReadAsync(Request, ct), request, adapter,
    options, serverUsers, reconciler.DeletedUserIds, snapshotDirectory, logger, ct);
```

- **`ExportEnvelope`**: `{Format: "jellyfin-plugin-export/1", Plugin, PluginId, PluginVersion, SchemaVersion, ExportedUtc,
  ServerId, Users:[{Id (N), Name, Deleted?}], Settings, Data, Archive:[…]}`. At most 100 MB (413); every field checked
  (400); another plugin's export 400, a newer schema 409.
- **`JsonStoreImport`**: data migrated (`ReadData`); users matched by id, or by name (ignoring case) where *Remap by name*'s
  refusals hold, plus `DeletedOnExportingServer`; unmatched users' records skipped with a reason. The plugin's
  `JsonImportAdapter.Apply` merges (`ImportMerge.Merge`: by id, a strictly newer `UpdatedAt` wins, nothing removed) or
  replaces. A dry run writes nothing; a real one first asks `JsonImportAdapter.CheckBeforeWrite` (a refusal there takes
  no snapshot), then takes an `import` snapshot and applies under the writer lock. Settings
  are never applied. Archived records go to `DetachedRecords.ImportArchived`.

Tests: Kit.Tests `Users/` and `Storage/` (`ExportEnvelopeTests`, `JsonStoreImportTests`); in a sandbox
`scripts/users-probe.mjs` through Bookmarks' developer user data demo.

### Item identity (`Library/`)

Item ids are MD5(type + path), so a move or rename gives an item a new id; a record keeps an `IdentitySnapshot` next
to its `ItemId` so it can be found again.

```csharp
var identity = ItemIdentity.Capture(item, libraryManager);             // when written, and on ItemUpdated
if (ItemIdentity.IsRemovedBy(record.Identity, removed.Id)) { /* detach */ }   // the item, its series, season or album
var result = resolver.Resolve(record.Identity, RelinkPolicy.RuntimeWithin(),
    attachedUtc: record.AttachedUtc, isVisible: IdentityResolver.VisibleTo(libraryManager, owner));
if (result.ShouldRelink) { /* ItemId = result.ItemId; AttachedUtc */ }
var catchUp = moveJournal.CatchUp(doc.MoveJournalId, doc.LastMoveSeq);  // at startup; store the new position after
```

- **`IdentitySnapshot`**: `ItemId`, `Kind`, `Name`, `Year`, `ProviderIds`, `SeriesProviderIds`, `SeriesName`, `Season`,
  `Episode`, `SeriesId`, `SeasonId`, `AlbumId`, `RunTimeTicks`, `Path` (admin-only), `Extra`. `Capture` reads parents
  through the given `ILibraryManager`, never with `Guid.Empty` (which throws on 10.11.5), and works in an `ItemRemoved`
  handler.
- **Cascading removals.** A removed folder raises `ItemRemoved` once, for the folder only; `IsRemovedBy` covers the item,
  series, season and album; other folders need the verify sweep.

**`IdentityResolver.Resolve`**, in order: (1) the id, **Exact**, if its parents reach the library (10.11.5's cache still
returns a deleted artist's tracks); (2) the move journal chain from the id, counting only lines after `attachedUtc`:
**Exact**, or a **pending relink** while the last line is under 7 days old; (3) the path, **Exact**, only for a
non-blank path and an item of the expected kind (10.11.5's `FindByPath("")` answers the newest item); (4) the kind's
identifying provider ids (`Tmdb`/`Imdb`/`Tvdb` for videos, MusicBrainz and AudioDb ids for music), or an episode's
numbers under a series sharing an id: **Strong** (a contradicting id makes it **Weak**); (5) only without Strong: same
kind, name and year, **Weak** suggestions (at most 5). Exact always relinks; a single Strong relinks when the policy and
its validator accept it, else `Ambiguous`; Weak never. The resolver writes nothing.

- **`attachedUtc`**: a new file at a moved item's old path gets the old id; a record on it must not follow the old move.
- **`isVisible`**: a user's record never relinks to an item that user can't see (`VisibleTo(library, owner)`).
- **`RelinkPolicy`**: `Default`, `ExactOnly`, `RuntimeWithin(tolerance = 0.02, cap = 60 s, requireStoredRuntime)`
  (Bookmarks; per record, through `GetRelinkPolicy`); `RefuseConflictingExact` (User Ratings) refuses an Exact match
  whose strong ids contradict.

**Move journal.** Library Maintenance's `<data>/librarymaintenance/moves.jsonl`, read-only (`FileShare.ReadWrite |
Delete`), never written by consumers: a header `{"V":1,"Header":true,"JournalId":…,"CreatedUtc":…}`, then lines `{"V":1,
"JournalId":…,"Seq":…,"Status":"Committed","Utc":…,"OldId":…,"NewId":…,"Kind":…,"OldPath":…,"NewPath":…}` (plus `JobId`,
`Role`, `Correction`). Only `V` 1 Committed lines in rising `Seq` count; other statuses and versions are ignored, broken
lines counted, a torn last line skipped; any state but `Ok` relinks nothing. Chains follow `NewId` → `OldId` in `Seq`
order (a Move back ends where it started). `CatchUp` replays from 0 on a new `JournalId` or a `Seq` below the stored one,
idempotently. `kit/testdata/move-journal/moves-v1.jsonl` is the contract fixture.

Tests: Kit.Tests `Library/` (the resolver's order, the journal, `CascadingRemovalTests` on `FakeLibrary`); in a sandbox
`scripts/identity-probe.mjs`.

### Background work and library changes (`Hosting/`, `Library/`)

```csharp
internal sealed class RatingsWorker(IServerApplicationHost host, ILogger<RatingsWorker> logger) : KitBackgroundService(host, logger)
{
    protected override async Task RunAsync(CancellationToken stoppingToken) { /* after the startup gate */ }
}

internal sealed class RatingsLibraryConsumer : ILibraryChangeConsumer
{
    public string Name => "Ratings";
    public LibraryChangeFilter Filter { get; } = new()
    {
        Kinds = new HashSet<BaseItemKind> { BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Episode },
        Ignore = (kind, item) => false,   // the plugin's own items, O(1)
        IncludeUserData = false,          // opt-in: UserDataSaved, without playback progress
    };

    public async Task HandleAsync(LibraryChangeBatch batch, CancellationToken ct)
    {
        // Removed → the verify sweep when batch.RemovedFolders isn't empty → Added/Updated, in every batch.
        // DuringPostScan: IsScanRunning is true on purpose. AfterFullPass: Jellyfin's own post-scan tasks' changes.
    }
}
```

- **`StartupGate`** waits for `CoreStartupHasCompleted` (after Jellyfin's startup tasks, which locate ffmpeg), polled
  every second. Nothing heavy runs before.
- **`KitBackgroundService`**: an exception escaping a `BackgroundService` stops Jellyfin 10.11.5. The kit's base waits
  on the gate, runs `RunAsync`, catches everything but shutdown, and restarts after 30 s, doubling to 30 min.
  `KitAdoptionTests` requires every plugin `BackgroundService` to derive from it.
- **`LibraryChangeMonitor`** subscribes after the gate to `ItemAdded`, `ItemUpdated`, `ItemRemoved` (and
  `UserDataSaved` on request). Handlers are O(1) and never throw; they filter per consumer (library items only, `Kinds`,
  `Ignore`), always pass removed folders (`RemovedFolders`), and capture a removed item's identity and top parent.
  Changes queue per item (added then removed is only `Removed`), never holding items.
- **Timer batches** go out after 60 s quiet or 10 min at most, never while `IsScanRunning`.
- **Post-scan flush.** Jellyfin runs the plugin's `KitPostScanTaskBase` once per scan while `IsScanRunning`: it flushes
  every consumer synchronously as one full pass (`FullPass`, `DuringPostScan`). Plugins' post-scan tasks run before
  core's (artists, genres, studios, collections, clean-up), so changes those make go out as one `AfterFullPass` batch as
  soon as the scan ends.
- Each consumer is a `KitComponent`, called in order inside its own try/catch. Events before the gate or during shutdown
  aren't replayed: the verify sweeps close the gap. Diagnostics `LibraryChanges` has the counts and the last ten batches.

Tests: Kit.Tests `Hosting/` and `Library/` (a real `Host` that a plain throwing service stops and a kit service doesn't;
one post-scan task across three scans); in a sandbox `scripts/library-changes-probe.mjs` against real scans.

### Record lifecycle and library reads (`Library/`, `Hosting/`)

A record pointing at a library item is attached (`ItemId`, `IdentitySnapshot`, `AttachedUtc`) or detached
(`DetachedUtc`). Each store is an `IAttachedRecordOwner` and `IDetachedRecordOwner`, added with `AddKitRecordOwner<T>()`.

```csharp
foreach (var record in attached.Where(r => ItemIdentity.IsRemovedBy(r.Identity, removedId))) { /* DetachedUtc = now */ }
if (batch.RemovedFolders.Count > 0) await verifier.SweepAsync(VerifySweepReason.RemovedFolders, ct);
if (batch.Added.Count + batch.Updated.Count > 0) await detached.ResolveDetachedAsync(owner, ct);

var query = access.CreateQuery(user, requestedLibraryIds);      // null: no library left, answer with nothing
var rows = VersionSets.Collapse(matches, ids => access.EligibleIds(user, libraries, ids), VersionSets.LibraryLookup(library));
fresh.Update(itemId, user, d => { d.Rating = 7; return true; }, UserDataSaveReason.UpdateUserData, ct);
```

- **Verify sweep** (`AttachedRecordVerifier`): every distinct attached id looked up once (batches of 500 with a yield),
  misses detached with their snapshots; pending journal relinks wait 7 days; lookup errors detach nothing. At startup,
  after removed folders and daily; sweeps never overlap.
- **`DetachedRecords`**: re-resolves detached records with the owner's policy and the user's view; archives records
  detached longer than `DetachedRetentionDays` into `archive/<store>-archived.jsonl` (written and flushed before removal);
  `ArchiveForReasonAsync` archives for the owner's own reason (Bookmarks' `ClipMissing`); reattaches up to 500 archived
  records per run; **`PurgeAsync` (`POST /<Route>/Admin/Detached/Purge`) is the only hard delete**, after a `purge`
  snapshot and a copy of the archive (`includeHistory` also removes earlier lines, so none of a deleted user's text
  stays; 409 read-only, 400 a negative age, 503 an unreadable archive, checked before anything is deleted); exports and
  imports the archive (503 while it can't be read). Diagnostics `Verifier`, `Detached` and `Maintenance` show the runs.
- **`DetachedArchive`**: JSON Lines, `V` 1, a rising `Seq`, never rotated: `Archived` (the record in its store's shape,
  optional `Reason`), `Reattached`, `Superseded`, `Checked` lines; rewritten only through a flushed `.tmp`.
- **`LibraryAccess`**: the user's visible libraries; `CreateQuery(user, ids)` = `new InternalItemsQuery(user)` with the
  libraries' `PhysicalFolderIds` as `TopParentIds`, no core grouping, never `HasOwnerId`, **null** when no library is
  left; `EligibleIds` also checks every parent as `GetItemById<T>(id, user)` does, so a TV-PG episode of a TV-MA series is
  hidden from a TV-14 user.
- **`VersionSets`**: `Group` reads merged versions as sets (broken chains, stale links, cycles); `Collapse` keeps one row
  per set.
- **`FreshUserData`**: the only way to read `UserItemData` before a read-modify-write; reloads the item's rows onto core's
  cached instance. Null: write nothing. It checks no access.
- **Maintenance** (`KitMaintenanceTaskBase`): "<Plugin>: Maintenance", daily, staggered (Bookmarks 03:10, User Ratings
  03:20, Access Roles 03:50). Steps in order, each on its own: snapshots (100), `Archive` (200), `Users` (300),
  `VerifySweep` (400), `ResolveDetached` (500), `Reattach` (510), the plugin's (600), SQLite's `Checkpoint` (900).
  `AddKitMaintenanceGate` holds the plugin's own work during the run (User Ratings' data jobs).

Tests: Kit.Tests `Library/` and `Hosting/`; in a sandbox `scripts/library-demo-probe.mjs` through Bookmarks' developer
library demo.

### Jellyfin Enhanced interop (`Interop/`, `kit.shortcuts`)

What the kit reads of Jellyfin Enhanced (JE), Custom Tabs and Plugin Pages, read-only, and what every plugin gets from
it: the shortcut policy, the overlap banner, the switch-off checklist and "Use B".

```csharp
JellyfinEnhanced = new JeInteropOptions                                  // in KitOptions
{
    Overlap = JeFeature.Bookmarks,                                        // or Reviews (User Ratings), or None
    Shortcuts = s => [new JeShortcutSetting("BookmarkNow", "Bookmark this moment", Config(s).BookmarkNowShortcut)],
    ShowBanner = s => Config(s).ShowJellyfinEnhancedBanner
},
serviceCollection.AddKitJellyfinEnhanced();                     // after AddKitDiagnostics
ReservedShortcuts = _shortcuts?.ReservedCombos(user.Id) ?? [],  // capabilities: the caller's own JE overrides only
```

- **`JellyfinEnhancedReader`** (memoised 500 ms): JE's state from `IPluginManager`; from `Jellyfin.Plugin.JellyfinEnhanced.xml`
  only `JellyfinEnhancedDefaults.Flags` (the review and activity-feed settings, `DisableAllShortcuts`, the six
  `Bookmarks*` settings; a missing one reads JE's default), whether the `PresenceOnly` settings (`ReviewTranslationUrl`,
  `ReviewTranslationApiKey`) are set (never their values), and the `Shortcuts` list; no other setting is read. Also
  each user's `shortcuts.json`, Custom Tabs' XML, Plugin Pages' list in memory, and for the importers `reviews.json` and
  `bookmark.json` (`JeDataParser`: JE 12.8–12.12's shapes, bad entries left out with a reason, other files
  `Unreadable` with a plain reason). Files are opened read-only with sharing, XML without DTDs; **the kit never writes,
  moves or deletes them, and never changes JE's settings**. `kit/testdata/je-data` holds the synthetic files the tests
  parse, and two hand-written stand-ins for JE's settings file: `Jellyfin.Plugin.JellyfinEnhanced.xml` (12.11's shape:
  the settings the kit reads and made-up fillers) and `Jellyfin.Plugin.JellyfinEnhanced.12.12.xml` (the same plus
  made-up settings a newer version adds; below, "Jellyfin Enhanced 12.12").
- **How JE turns settings into keys** (`JeShortcuts`): one key per action, the user's list over the server's; `""` is off.
  At every start JE drops server entries without a key and **puts the default back for any action left without one**
  (`BackfillMissingDefaultShortcuts`), so an admin's cleared key returns as `B` after a restart, while any other key
  (`Disabled`, `Shift+X`) stays. JE compares whole combinations, so `Shift+B` leaves `B` free; 12.12 also reads `b` as
  `B`. `ShortcutCombo.JellyfinEnhancedCombos` reads keys both ways.
- **`ShortcutPolicy`**: jellyfin-web's video OSD keys and JE's effective keys (or its defaults when JE isn't installed);
  `Check(setting)` gives the settings page's warning ("`B` is taken by Jellyfin Enhanced's Bookmark Current Time, …").
  `ReservedCombos(userId)` never includes another user's overrides; `AllReservedCombos()` is for admin Diagnostics only.
  `ShortcutCombo` and `kit.shortcuts.normalize` share the notation of `kit/testdata/shortcuts/combos.json`.
- **`JeSwitchOff`**: the banner while JE's feature is on, and the checklist (`JeChecklistItem { Key, Text, Done, Detail,
  NeedsRestart, Optional, Unknown }`). Bookmarks: `BookmarksAutoCreateCustomTab`, `BookmarksUseCustomTabs`,
  `BookmarksUsePluginPages`, `BookmarksEnabled` off; no JE Custom Tab (`<div class="sections bookmarks"></div>`); no
  Plugin Pages link (`NeedsRestart` while listed); `ShortcutAdmin`, JE's `BookmarkCurrentTime` moved off `B`, worded for
  12.11's Keyboard tab (type `Disabled`) or 12.12's grid of keys (`HasShortcutGrid`: click the B key at the left end of
  the row, press another combination; never Delete or ⊘), a cleared key ticked with `NeedsRestart`; `ShortcutUsers`, no
  user's own `B`. **`CanUseB`** only when both shortcut items are durably done; `UseBNote` says why not. Reviews (User
  Ratings): required `ShowUserReviews`, `ReviewTranslationEnabled` (with its URL and key cleared),
  `MirrorReviewRatingsToJellyfin` and `ActivityFeedShowReviewed` off; optional `ShowReviews` (traps the TV D-pad) and
  `ShowUserRatingOnPosters`; for information `ShowAnilistReviews`, `ReviewsExpandedByDefault`,
  `HideReviewsFromHiddenUsers`, `HideReviewsFromDisabledUsers`, `SpoilerStripReviews`. An unreadable settings file makes
  every item `Unknown`, never done.
- **Diagnostics `JellyfinEnhanced`**: the state, the read settings, effective shortcuts, users' overrides, Custom Tabs,
  Plugin Pages, `Overlap` (banner, checklist, `CanUseB`) and `OurShortcuts` with warnings. The settings page renders it;
  `CheckShortcut(name, combo)` checks a key before it is saved.
- **Browser**: Bookmarks binds `Shift+B` by default (JE's capture-phase listener takes `B`) in the bubble phase, never
  in the capture phase on `window` or `document` (it would act on JE's keys too), except inside its own scoped dialogs.
- Tests: `tests/Kit.Tests/Interop`, `web/tests/shortcuts.test.mjs`; in the `je` sandbox `scripts/je-switchoff-probe.mjs`
  with `webchecks/je-*.json` (`scripts/README.md`).

### Jellyfin Enhanced 12.11

Written against JE 12.8, checked against 12.11's DLL and in the `je` sandbox. Unchanged: the shortcut list (20 defaults,
`BookmarkCurrentTime` = `B`), `BackfillMissingDefaultShortcuts` (the same IL), the per-user files and folders,
`reviews.json` and `bookmark.json`, the Plugin Pages and Custom Tab handling, and the `document` capture-phase key
listener comparing `Shift+Meta+Ctrl+Alt+Key` whole. Settings XML: the same names and defaults (332 properties in all;
`kit/testdata/je-data/Jellyfin.Plugin.JellyfinEnhanced.xml` is a hand-written stand-in with the ones the kit reads);
new and read by the kit, `MirrorReviewRatingsToJellyfin` (off by default) writes a review's stars into Jellyfin's own
user rating. So the design stands: `Shift+B` by default, the checklist, and "Use B" once JE's key is durably off `B`.

### Jellyfin Enhanced 12.12

JE's auto-update installs a new version beside the old, loaded at the next restart; the kit reports the loaded one.
Checked against 12.12's DLL and in the `je` sandbox:

- Settings XML: 351 properties, none renamed, moved or removed; the ones the kit reads unchanged;
  `kit/testdata/je-data/Jellyfin.Plugin.JellyfinEnhanced.12.12.xml` is a hand-written stand-in (the 12.11 one plus
  made-up added settings). Shortcut defaults, the start-up backfill and the data files unchanged.
- Key listener: the same phase, but keys are normalised before the compare (`b` takes `B`, modifiers in any order);
  `Shift+B` still isn't `B`. The policy reserves what either version acts on.
- Keyboard tab rebuilt as a grid of keys (Delete and ⊘ save a blank key; no text can be typed): the checklist's key step
  follows the version the server runs.
- Plugin Pages' link is also rewritten on every save of JE's settings (Plugin Pages 2.4 still needs a restart).
- New features (Catch Up, tag colours, MDBList sources, per-season providers) don't overlap bookmarks or reviews.

### Jellyfin Enhanced 12.14

Checked through a running `je` sandbox only: the fixture check passes as on 12.12 and the key checks give 12.12's
verdicts. 360 settings; new review settings the kit reads: `ReviewTranslationEnabled`, `ReviewTranslationUrl`,
`ReviewTranslationApiKey` (an outside translation service, off and empty by default) and `ShowAnilistReviews`. The flag
defaults and data formats weren't re-read on 12.14. On the TV layout, where JE draws its review section, the D-pad
doesn't leave the details buttons row (User Ratings' Rate button is the way down).

## Test support (`testsupport/`)

| Helper | Used for |
| --- | --- |
| `Api/SchemaPrefixChecks` | Every type Swashbuckle reaches from a controller starts with `SchemaPrefix`, no schema id twice, none in `testdata/openapi-schema-ids.txt` (checked against Swashbuckle 6.2.3; `scripts/api-docs-probe.mjs` repeats it on the live document). |
| `Api/ControllerNameChecks` | Controller names start with `RoutePrefix`, none in `testdata/reserved-controller-names.txt`; every route under `/<RoutePrefix>`. |
| `Api/AnonymousActionChecks` | Only `GET .../ClientScript` and `.../AdminScript` may be anonymous, counting policies that admit callers without a token (`AnonymousLanAccessPolicy`, `LocalAccessOrRequiresElevation`, `FirstTimeSetup*`). |
| `Api/PublicBoundaryChecks`, `MethodBodyReferences` | Every public type has a role (controller, a part Jellyfin creates, configuration, service interface, DTO in `Api.Models`, Integration facade) or is reported; signatures expose nothing internal; a controller's IL touches no internal plugin type. `Describe` feeds the pinned-surface test. |
| `Api/JellyfinAuth` | Jellyfin 10.11.5's claims and `RequiresElevation` for controller tests. |
| `PluginIdentity/*` | `build.yaml` against source, project, assembly and `GetPages()`. |
| `Storage/StoreConformanceTests` | The JSON store suite (writes, quarantine, versions, both failed-migration cases, unknown fields, snapshots, every row of the load table, restore, export, the plugin's fixtures, the 5 MB timing). |
| `Web/WebHostHarness` | Jellyfin 10.11.5's web pipeline in process, with `WithMvc` (validation, auth, Jellyfin's nullable-enum binder). |
| `Web/JellyfinEnhancedLikeStartupFilter`, `MiddlewareStartupFilter` | A filter acting like JE 12.8's `index.html` middleware; an inline middleware at a position. |
| `Web/KitScriptChecks`, `WebCouplingChecks`, `DashboardPageChecks` | `node --check` and the kit's JS checks of a built script; the coupling policy; dashboard pages that set HTML only from string literals. |
| `Library/FakeLibrary` | An in-memory library behind a mocked `ILibraryManager` that behaves like 10.11.5 where it matters (`Guid.Empty` throws, a blank `FindByPath` returns the newest item, a removed folder raises one `ItemRemoved`). |
| `TempDirectory`, `ListLogger`, `ManualTimeProvider`, `ManualTimerProvider` | A temporary folder, a logger that keeps entries, a clock and timers that move only when told. |

Every plugin's tests run the API and boundary checks and pin their public surface (Repo.Tests
`ApiConventionCoverageTests`).

## Pinned lists (`testdata/`)

- `openapi-schema-ids.txt`: 409 schema ids of Jellyfin 10.11.5 (357) and a reference set of community plugins (52; its
  header says how it was made). Refresh it from a sandbox's `GET /api-docs/openapi.json` after an update.
- `reserved-controller-names.txt`: every 10.11.5 controller name plus `HomeScreen` (keyed on by JE 12.8's filters).
- `je-sandbox/`: the `je` sandbox settings fixture (see its README).
- `je-data/`: synthetic Jellyfin Enhanced data files (`reviews.json`, two users' `bookmark.json`, a `shortcuts.json`,
  hand-written stand-ins for the 12.11- and 12.12-shaped settings XML) in the shapes JE 12.8 to 12.12 write, with the odd
  entries the parser must leave out, and `import-81/` for Bookmarks' import (see its README); never a server's own files.
- `shortcuts/combos.json`: the key-combination cases `ShortcutCombo` and `kit.shortcuts` both pass.
- `move-journal/moves-v1.jsonl`: the move-journal contract fixture (see its README).
- `web/index.html`: a synthetic page shaped like jellyfin-web 10.11.5's `index.html` for `WebHostHarness` (see
  `web/README.md`). No jellyfin-web code is copied.
