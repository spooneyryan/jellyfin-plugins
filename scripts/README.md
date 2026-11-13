# Scripts

Build, deploy, sandbox and check tooling for every plugin in this repository. Run the scripts from the repository root
in Windows PowerShell 5.1 (or PowerShell 7) and Node 22. The PowerShell scripts share `JellyfinSandbox.psm1`.

- [Quick start](#quick-start)
- [Safety rules the scripts enforce](#safety-rules-the-scripts-enforce)
- [Layout](#layout) and [Standard sandboxes](#standard-sandboxes)
- [Sandbox and server scripts](#sandbox-and-server-scripts): `dev-server.ps1`, `Stop-Sandbox.ps1`, `Deploy-Plugin.ps1`,
  `Initialize-Sandbox.ps1`, `New-SandboxMedia.ps1`, `Set-SandboxPluginConfig.ps1`, `JellyfinSandbox.psm1`
- [webcheck.mjs](#webcheckmjs): the headless browser runner, its JSON steps, request policy and redaction
- Checks per family: [Kit and web hosting](#kit-and-web-hosting-checks), [Bookmarks](#bookmarks-checks),
  [User Ratings](#user-ratings-checks), [Library Maintenance](#library-maintenance-checks),
  [Access Roles](#access-roles-checks), [Smart Search](#smart-search-checks), [Tag Cloud](#tag-cloud-checks)
- [Release and install tooling](#release-and-install-tooling): [releasing a plugin](#releasing-a-plugin)
  (`Package-Release.ps1`, `Test-ReleaseCatalog.ps1`, `serve-release.mjs`), `Install-RealServer.ps1`
- [Troubleshooting](#troubleshooting)

**What you need:** Windows, an installed Jellyfin 10.11.x in `C:\ProgramData\Jellyfin\Server` (only read: its
`jellyfin.exe`, `ffmpeg.exe`, `jellyfin-web` and community plugin folders are copied; `-JellyfinDir` points elsewhere),
the .NET SDK the root [DEVELOPMENT.md](../DEVELOPMENT.md) names, Node 22, and Chrome or Edge for the webchecks. Free
memory is limited: run one sandbox at a time and stop it when done.

## Quick start

```powershell
./scripts/dev-server.ps1 -Name default -Background        # build (Debug), deploy, start, wait, check the log
./scripts/Initialize-Sandbox.ps1 -Name default            # wizard, users, media and libraries, scan, download-task and /api-docs checks (safe to re-run)
node scripts/index-probe.mjs --sandbox default            # the index.html injection over raw HTTP
node scripts/webcheck.mjs --sandbox default --steps bookmarks-ui.json --out result.json   # a webcheck (steps in scripts/webchecks)
node scripts/bookmarks-api-probe.mjs --sandbox default    # an API probe: one JSON result, exit 0 when every check passed
./scripts/Set-SandboxPluginConfig.ps1 -Name default -Plugin Bookmarks -Set @{ EnablePageDemo = $true }   # change a setting
./scripts/Stop-Sandbox.ps1 -Name default
```

- **Prerequisites.** Windows with Windows PowerShell 5.1, a local **Jellyfin 10.11.5** install (the sandboxes run its
  `jellyfin.exe`, web client and bundled `ffmpeg.exe` from its program folder, only read: `C:\ProgramData\Jellyfin\Server`
  by default, another with `-JellyfinDir`), the .NET SDK (`RELEASE_SDK` in `.github/workflows/ci.yml`), Node.js 22, and
  Chrome or Edge for the webchecks. Tested on Windows only.
- **Seeding.** `Initialize-Sandbox.ps1` starts the sandbox when it isn't running and generates the media
  (`New-SandboxMedia.ps1`) when it is missing, so it alone seeds a new sandbox.
- **Other sandboxes.** `-Name je` (ours next to Jellyfin Enhanced), `bare`, `baseurl`, `move`, `access`: see
  [Standard sandboxes](#standard-sandboxes). Access Roles runs alone:
  `./scripts/dev-server.ps1 -Name access -Port 8103 -Plugins AccessRoles -Community FileTransformation,PluginPages -Background`.
- **Results and redaction.** Every probe and webcheck prints one JSON result (`--out <file>` also saves it). Session
  tokens and API keys never appear in it: webcheck results are redacted (`redact.mjs`), probes never print the tokens
  they read from `credentials.json` or the keys and passwords they create. Item, user and device ids are kept.
- **Which check for which change.** Each plugin's `DEVELOPMENT.md` lists the sandbox checks for its areas; the family
  sections below say what each script and webcheck covers and what it needs.

## Safety rules the scripts enforce

- **The real server is never touched.** Every sandbox path (data, config, plugins, cache, log, web, temp) is refused
  when it resolves under `C:\ProgramData\Jellyfin` in any spelling (`C:\PROGRA~3\…`, `..`, `\\?\C:\…`, a junction,
  symbolic link, `subst` or mapped drive on the way: the deepest existing folder is resolved with
  `GetFinalPathNameByHandle`; a PowerShell drive is followed to its folder first, `ConvertTo-FileSystemPath`). Shares
  and other non-drive paths (`\\localhost\C$\…`) are refused outright. Port 8096 is refused, and so is any port in use.
- **The real installation is only read**: `jellyfin.exe`, `ffmpeg.exe`, a copy of `jellyfin-web`, copies of community
  plugin folders. Its settings and data files are never copied.
- **The real server gets a release tag's build, never the working tree** (see
  [Install-RealServer.ps1](#install-realserverps1)): main carries unreleased work under the released version number
  between releases.
- **Bookmarks' clip paths stay in the sandbox.** Bookmarks writes clips into, and deletes files from, the folders its
  data names, and deletes stale `.partial` files in every recorded clip folder at each start. So before anything is
  written, `dev-server.ps1` reads every `data/data/bookmarks/bookmarks.json*` (the store, `.bak`, `.v1-*` copies, a
  `.tmp`) and `ClipOutputPath` in the settings XML, and refuses to start while any clip path (a `ClipRoots` entry, a
  clip's `FilePath`, a `PendingFileDeletes` path, `ClipOutputPath`; a relative one resolved against the sandbox folder)
  is not on a local drive, is under `C:\ProgramData\Jellyfin`, or is outside `.sandbox/<name>`. A media file's path
  (`Identity.Path`) isn't a clip path. In a file `ConvertFrom-Json` can't read, no drive path may be under
  `C:\ProgramData\Jellyfin`. The error lists each file, field and path (`Test-SandboxClipPaths`). Snapshots aren't
  read: restoring one is an admin's action, and the next start checks the restored file.
- **Every server path is explicit**: `--datadir`, `--configdir`, `--cachedir`, `--logdir`, `--webdir`, `--ffmpeg`, plus
  `TMP`/`TEMP`, so a sandbox never shares `%TEMP%\jellyfin` with the real server. Inherited `JELLYFIN_*` variables are
  cleared. After startup the server log is checked and the sandbox is stopped at once if a reported path is outside it.
- **Loopback only**: `network.xml` binds 127.0.0.1 on the sandbox port, with auto-discovery, UPnP and remote access off.
  The plugin repositories are emptied, so a sandbox never downloads or updates plugins.
- **No downloads from the internet.** Binding to loopback doesn't stop outbound requests, so three layers apply:
  - *The tasks that would reach the internet have no triggers.* Jellyfin's "Update Plugins" (3 s after every start; a
    new sandbox has the default `repo.jellyfin.org` repository until `Initialize-Sandbox.ps1` empties the list) and,
    with Jellyfin Enhanced (JE), its "Refresh CDN Assets" (about 57 assets from jsDelivr, flagcdn, Wikimedia and
    others, at every start and every 24 hours) and "Send Anonymous Usage Report" (to a `supabase.co` host when JE's
    `AnalyticsEnabled` is on). Before every start `dev-server.ps1` writes `[]` to
    `data/config/ScheduledTasks/<id>.js`, the file Jellyfin reads instead of a task's default triggers (`<id>` is the
    MD5 of the task's type name: `f9b057c0-54e9-e6da-ee4a-88ffd146a403`, `e32160f3-c9e0-dcb2-33c8-11fe73c65b1a`,
    `f3e92136-da16-23c2-26a0-c0f15a08f3b7`). The list is the task table in `JellyfinSandbox.psm1`. A task can still be
    started by hand from a sandbox's dashboard.
  - *Settings that keep the rest of JE quiet.* JE's other outbound tasks (Seerr imports and watchlist syncs, MDBList
    ratings, Sonarr/Radarr tag sync) keep their triggers but skip without a URL or API key, and no sandbox has one: the
    `je` fixture sets none and pins `JellyseerrEnabled` and `AnalyticsEnabled` off; other sandboxes run JE's defaults.
    A key or URL set by hand leaves only the dead proxy in between.
  - *A dead proxy for the server process.* `jellyfin.exe` starts with `HTTP_PROXY`/`HTTPS_PROXY` (both cases) set to
    `http://127.0.0.1:9`, where nothing listens, and `NO_PROXY=127.0.0.1,localhost`. Jellyfin's and JE's HTTP clients
    use .NET's default proxy, which reads these variables, so an outbound request is refused without a DNS lookup while
    the sandbox's own URL goes direct. This covers a task started by hand, JE's CDN proxy, a key set by hand and tasks a
    future version adds. The variables are set only for the server's start (`Get-SandboxServerEnvironment`,
    `Invoke-WithChildEnvironment`); user and machine variables never change. Not covered: a client with its own proxy or
    `UseProxy = false`, raw sockets, and non-HTTP URLs handed to ffmpeg.

  JE also fetches a missing asset when a page asks its CDN proxy (`/JellyfinEnhanced/cdn/…`), so `webcheck.mjs` blocks
  that route (`sandbox-policy.mjs`). In your own browser JE tries, the dead proxy refuses, the route answers 404, and JE
  carries on without the asset.
- **Only our own processes are stopped**, by recorded PID, after checking the command line (the sandbox's
  `--datadir`, or its browser profile folder). Nothing is stopped by name.
- **Sandbox passwords** are generated, stored only in `.sandbox/<name>/credentials.json` (gitignored) and never printed.

## Layout

```
.sandbox/
  media/                 shared test media (New-SandboxMedia.ps1), C: only
  <name>/
    data/                --datadir (config/, plugins/, data/ ...); config/ScheduledTasks/ holds the empty trigger lists
    cache/ log/ temp/    --cachedir, --logdir, TMP/TEMP
    web/                 copy of the installed jellyfin-web (--webdir), refreshed when it changes
    sandbox.json         port, BaseUrl, plugins, community plugins, configuration and paths of the last start
    server.pid           PID of the running server
    credentials.json     admin, viewer, viewer2: ids, passwords, tokens
    browser/             webcheck.mjs profiles (temporary) and screenshots
    <probe folder>/      a probe's state and results (below)
```

A probe that changes anything keeps its state in its own folder under `.sandbox/<name>/`, named after the script
(`bookmarks-clips-probe/`, `identity-probe/`, ...) except `api-probe/` (`bookmarks-api-probe.mjs --keep-files` and
`userratings-api-probe.mjs`), `visibility-probe/`, `details-probe/`, `lifecycle-probe/` (the User Ratings probes),
`ur-je-import-probe/` and `bookmarks-je-web-probe/` (`bookmarks-je-web-probe.mjs`). The common files:

| File | What it is |
| --- | --- |
| `moves.json` | the undo list of media or clip files moved or folders renamed aside, written before each move |
| `held/`, `hold/` | media or clip files held out during a run |
| `created.json` | folders or records the probe made, removed at the end |
| `saved-settings.json` | plugin settings to put back |
| `state.json`, `test-user.json` | users, libraries and ratings it made (ids and names only, no secret) |
| `backup/`, `store-backup/`, `drill-backup/` | data files as they were, put back with the sandbox stopped |
| results, screenshots, `script-output-<n>.log` | its webchecks' results and the restart scripts' output |

The next run of a probe that was killed half way undoes what the files record first.

## Standard sandboxes

| Name | Port | Plugins | Purpose |
| --- | --- | --- | --- |
| `default` | 8097 | ours + File Transformation + Plugin Pages | normal development (our plugins don't need FT; Plugin Pages does) |
| `je` | 8098 | ours + FT + Plugin Pages + Jellyfin Enhanced + Custom Tabs + JavaScript Injector (another `index.html` middleware, which sorts before JE), with the pinned settings fixture (`kit/testdata/je-sandbox`) | coexistence |
| `bare` | 8099 | ours only | our web UI and fallback pages without FT or Plugin Pages |
| `baseurl` | 8100 | ours + FT + Plugin Pages, BaseUrl `/jf` | path handling |
| `move` | 8101 | ours + FT + Plugin Pages; Library Maintenance's move libraries (real-time monitoring on) | Library Maintenance's moves |
| `access` | 8103 | Access Roles + FT + Plugin Pages (`-Plugins AccessRoles -Community FileTransformation,PluginPages`) | access control |
| `upgrade` | 8119 | FT + Plugin Pages, ours as the rehearsal installs them (`-Plugins None` first) | Bookmarks' upgrade rehearsal |

Any other name works with an explicit `-Port`. Without `-Plugins`, `dev-server.ps1` deploys every plugin of the
checkout, Access Roles included.

## Sandbox and server scripts

### dev-server.ps1 and Stop-Sandbox.ps1

```powershell
./scripts/dev-server.ps1 -Name <name> [-Port <p>] [-Plugins All|None|<list>] [-Community None|FileTransformation,PluginPages,JellyfinEnhanced,CustomTabs,JavaScriptInjector] [-CommunityFrom <plugins folder>]
                         [-BaseUrl /jf] [-Fresh] [-Background] [-Configuration Debug|Release] [-NoBuild]
                         [-JellyfinDir <dir>] [-DataDir|-CacheDir|-LogDir|-WebDir <dir>] [-StartTimeoutSec 240]
./scripts/Stop-Sandbox.ps1 -Name <name> [-TimeoutSec 45] [-ExitTimeoutSec 30]
```

`dev-server.ps1` builds and deploys our plugins (through `Deploy-Plugin.ps1 -JellyfinDir <its -JellyfinDir>`, so the
reference-version check runs against the Jellyfin the sandbox runs), copies community plugins, writes the safety
settings, starts `jellyfin.exe` and checks its log.

| Option | Effect |
| --- | --- |
| `-Plugins` | default: every `plugins/*/build.yaml`; unselected plugins are removed from the sandbox |
| `-Port`, `-Community`, `-BaseUrl` | default to the standard table; a sandbox of another name takes them from its `sandbox.json` (so a restart without them keeps them), and its first run needs `-Port` |
| `-Community` | copies only the **newest** version folder of each from the real install, `autoUpdate` false and `imagePath` pointed at the copy in its `meta.json`; unrequested ones are removed. JavaScript Injector comes with no settings, so it injects no script but adds its `index.html` middleware |
| `-CommunityFrom` | a plugins folder to copy them from instead of the real install's, such as another sandbox's `data\plugins` (to run a cached version when the real install has a newer one the sandbox must not run); kept in `sandbox.json`, so a restart without it copies from the same folder |
| `-Configuration` | the build; without it `-NoBuild` takes the configuration the sandbox last ran (`sandbox.json` `Configuration`, and says so), otherwise Debug (`Resolve-SandboxConfiguration`) |
| `-NoBuild` | deploys what is in `artifacts/deploy/<Plugin>/<Configuration>` with the checkout's `build.yaml` |
| `-Fresh` | deletes the sandbox's data, cache, log, temp, browser folders and credentials (not the web copy) |
| `-Background` | starts the server detached, writes `server.pid`, and returns once `/System/Ping` answers and a new "Startup complete" log line appears (10.11's setup server answers `/System/Info/Public` during migrations, so that isn't enough). Without it the server runs in the console |
| `-StartTimeoutSec` | how long `-Background` waits (240) |
| `-JellyfinDir`, `-DataDir`, ... | another Jellyfin program folder or sandbox folders |

- Before **every** start it writes the empty trigger lists of the download tasks (see the safety rules) and prints one
  line per task, so a trigger added in a sandbox's dashboard is gone after the next start. It refuses to start while
  port 9 is in use, or while a Bookmarks clip path is outside the sandbox.
- Startup is detected log by log (`Get-StartupCompleteCounts`, `Test-NewStartupComplete`): Jellyfin keeps three logs,
  so a start that opens a new day's log deletes the oldest and a total count could never grow.
- For `je`, the pinned fixture is written before the first start; a `je` sandbox that started without it has to be
  recreated with `-Fresh`.
- **Release builds in a sandbox.** After `Install-RealServer.ps1` put a release build into a stopped sandbox, start it
  once with `-Configuration Release -NoBuild`; from then on `-NoBuild` keeps it. `-NoBuild` deploys the build left in
  `artifacts\deploy\<Plugin>\Release` with the checkout's `build.yaml`, so this works while the checkout's version is
  the tag's (otherwise Deploy's file-version check stops the start). A plain `dev-server.ps1` builds the working tree
  over it again.
- **Restarts keep the sandbox's port.** Without `-Port` a standard name gets its standard port, so every probe that
  restarts a sandbox (`Stop-Sandbox.ps1`, then `dev-server.ps1 -Name x -Port <sandbox.json's port> -Background
  -NoBuild`) passes `-Port` (`ScriptsTests.EveryProbeRestart_KeepsTheSandboxsOwnPort`). They pass no `-Community` or
  `-BaseUrl` (`powershell.exe -File` can't pass a list); `dev-server.ps1` reads both from `sandbox.json`.
- **`-Background` and a caller's pipes.** The server is started through `Start-ProcessWithLogs` (`CreateProcessW` with
  `PROC_THREAD_ATTRIBUTE_HANDLE_LIST` naming only `server.out.log`, `server.err.log` and NUL as standard input, no
  console window), not `Start-Process -Redirect…`, which passes every inheritable handle on: the server would keep the
  caller's pipes and a caller reading them to the end (Node's `spawnSync`, a CI step, `$(…)` in Git Bash) would wait
  until the server exits. So a script may run the start with pipes and read its output (`r.stdout` has "Sandbox
  'default' is up: …"). `scripts/tests/dev-server-launch.test.mjs` checks it with a child process instead of the
  server. The foreground start keeps `Start-Process -NoNewWindow`, sharing the console on purpose. Probes that restart
  a sandbox still send each start's output to a file of their own.

`Stop-Sandbox.ps1` asks the server to shut down through the API (admin token, loopback port owned by the recorded PID),
falls back to stopping that PID after re-checking its command line, and stops leftover `webcheck.mjs` browser processes
of that sandbox. It polls for the exit every 250 ms (`Wait-ProcessExit`) for up to `-TimeoutSec` after the request and
`-ExitTimeoutSec` more before it reports "still running"; a process started at another time under the same PID counts
as gone.

### Deploy-Plugin.ps1

```powershell
./scripts/Deploy-Plugin.ps1 -Plugin Bookmarks -DataDir .sandbox\default\data [-Configuration Release|Debug] [-NoBuild] [-JellyfinTarget 10.11|12] [-JellyfinDir <dir>] [-SourceRoot <checkout>] [-RealServer -ReleaseLock <reason>] [-PassThru]
```

Builds `plugins/<Plugin>` (Release by default), checks the output, and installs it into a data folder's
`plugins/<name>_<version>`. `dev-server.ps1` and `Install-RealServer.ps1` call it; you rarely need it directly.

- **Target.** The Jellyfin in `-JellyfinDir` (default `C:\ProgramData\Jellyfin\Server`, only read) must be the one
  `-JellyfinTarget` builds for (`10.11`, the default, is 10.11.x; `12` is 12.x, the kit's ABI-guard rule), read from
  its `jellyfin.dll` before anything is built. A `-JellyfinTarget 12` deploy needs a Jellyfin 12.x program folder.
- **DLL allowlist.** Publishes to `artifacts/deploy/<Plugin>/<Configuration>` and fails if the output holds any `*.dll`
  not in `build.yaml`'s `artifacts` (shipped server assemblies, SQLite and other dependencies).
- **Reference versions.** Fails if the plugin's DLLs reference an assembly at a newer version than the target ships,
  naming each (`… references Microsoft.Data.Sqlite 10.0.11.0, but the server ships 9.0.11.0`): such a reference can't
  bind, and the server stops at startup when DI touches it. Read reflection-only in a separate process; a reference
  neither side has is a warning. `ServerPackagePinTests` pins `Directory.Packages.props` to what 10.11.5 ships.
- **Install.** Checks the DLL's file version against `build.yaml`, copies DLL and PDB, removes other versions of the
  plugin and writes `meta.json` (UTF-8, no BOM, `autoUpdate: false`). Each copied file is checked by SHA-256 against
  the build that was checked (hashed right after the build); a difference removes the plugin folder again and stops.
  The hashes are printed (`Checked by SHA-256: …`); `-PassThru` returns them with the plugin folder (`Plugin`, `Name`,
  `Version`, `Configuration`, `Target`, `Files`, `Image`).
- **The image.** `plugins/<Plugin>/image.png` (rendered from `branding/`) is written as `image.png` next to the DLLs,
  checked by SHA-256 with them and named in `meta.json` (`"imagePath": "image.png"`); Jellyfin serves it at
  `GET /Plugins/{id}/{version}/Image` for the plugin's card on the Plugins page. It is this checkout's working tree's;
  with `-SourceRoot`, that checkout's (with `-RealServer`, the release tag's, read from git), or else the one committed at
  this checkout's HEAD, read from git, so a release tagged before the plugins had images gets one too
  (`Resolve-PluginImage`). `Image: image.png from …` says which. Without one, no image and no `imagePath`.
- **`-SourceRoot <checkout>`** builds another checkout's `plugins/<Plugin>` with its `build.yaml`, into a folder only
  this run uses (`artifacts\builds\<Plugin>-<Configuration>-<yyyyMMdd-HHmmss>-<id>`), so another build landing in
  `artifacts\deploy` meanwhile can't be installed instead. Each DLL and PDB there must be the compiler's output in that
  checkout's `obj\<Configuration>\<framework>` (`Assert-BuildIsCompilerOutput`), or nothing is installed. After the
  install the build is copied to `artifacts\deploy\<Plugin>\<Configuration>` for `-NoBuild`, and the run's folder is
  removed (also after a failure). It builds with `--disable-build-servers`, so the worktree can be removed right after.
  Not with `-NoBuild`.
- **`-NoBuild`** installs what is in `artifacts/deploy/<Plugin>/<Configuration>`, with the same checks: that is how a
  sandbox gets a special build (one published with `-p:KitAbiTarget=12` to watch the kit's ABI guard keep a plugin
  inert on 10.11, [kit/README.md](../kit/README.md); deploy a normal build afterwards).
- **The real server.** It refuses any data folder a running `jellyfin.exe` uses. `C:\ProgramData\Jellyfin\Server` also
  needs `-RealServer`, and `-RealServer` installs only a release tag's build: `-SourceRoot` must be the worktree
  `Install-RealServer.ps1` made for this run (HEAD at the commit of `<plugin>/v<x.y.z>` for the `x.y.z.0` its
  `build.yaml` says), built in Release, with nothing ignored in it (an ignored `<project>.csproj.user` changes the
  build), no assume-unchanged or skip-worktree files, and locked by git with exactly the reason `-ReleaseLock` names
  (`Assert-PluginReleaseCheckout`). Without `-SourceRoot` or `-ReleaseLock`, with `-NoBuild` or `-Configuration Debug`
  it refuses; `-ReleaseLock` without `-RealServer` too. `Install-RealServer.ps1` passes `-RealServer` for a stopped
  sandbox's data folder as well, so a rehearsal takes the same path.
- `-DataDir`, `-SourceRoot` and `-JellyfinDir` are resolved through the script's own PowerShell drives first, so a
  `New-PSDrive` onto `C:\ProgramData\Jellyfin` can't slip past the guard.

`SandboxToolingTests` runs it on synthetic plugins (a too-new reference refused, a matching one passed, a wrong target
refused for its `-JellyfinDir`).

### Initialize-Sandbox.ps1

```powershell
./scripts/Initialize-Sandbox.ps1 -Name <name> [-NoStart] [-SkipScan] [-SkipApiDocs] [-PlayerTracks] [-ScanTimeoutSec 900] [-AssertJeFixture]
./scripts/Initialize-Sandbox.ps1 -Name <name> -PreStart -DataDir .sandbox\<name>\data   # the je fixture only, before the first start
```

Seeds a sandbox; safe to re-run. It starts the sandbox unless `-NoStart`, and generates the media when it is missing.

- Completes the wizard through `/Startup/*`, empties the plugin repositories, creates `admin`, `viewer` (no download
  permission) and `viewer2`, and stores their passwords and tokens in `credentials.json`.
- Creates the libraries Movies, Movies 2, Shows and Home Videos over `.sandbox/media` (metadata and image fetchers,
  trickplay and chapter images off); `default` adds Music, `scale` Synthetic Movies and Synthetic Shows, every other
  sandbox LM Duplicates and LM Duplicates 2 (Library Maintenance's own). It scans and waits (`-SkipScan`,
  `-ScanTimeoutSec`). Re-run, it adds missing libraries and media (a `default` Music library made over another folder
  gets `.sandbox/media/Music` added).
- `-PlayerTracks` also makes a `Player Tracks` movie library (see `New-SandboxMedia.ps1`).
- For `je` it reads JE's settings and shortcuts, Plugin Pages' list and Custom Tabs' tabs back through the running
  sandbox, fails if any differ from `kit/testdata/je-sandbox/expected.json`, and checks that JE, Custom Tabs and
  (when present) JavaScript Injector are Active. Another sandbox given the fixture with `-PreStart` gets the same check
  with `-AssertJeFixture`.
- In every sandbox: **the download tasks** must be listed by `GET /ScheduledTasks` with the id their trigger file is
  named after, no triggers, idle, and no run since the server started (`StartedUtc` in `sandbox.json`), and JE's
  `cdn-cache` must have no new file (`Test-SandboxTasksOff`). Otherwise it fails and says what to do (a renamed task
  type means updating the task table in `JellyfinSandbox.psm1`; a trigger means restarting with `dev-server.ps1`).
- **`GET /api-docs/openapi.json` must answer 200** (`api-docs-probe.mjs --summary`); `-SkipApiDocs` skips it.

### New-SandboxMedia.ps1

```powershell
./scripts/New-SandboxMedia.ps1 [-Force] [-MediaRoot <dir on C:>] [-FfmpegPath <ffmpeg.exe>] [-SyntheticMovies <n>] [-SyntheticEpisodes <n>] [-SyntheticMode HardLink|Stub] [-SyntheticSeed <n>]
```

Generates the shared test media (about 80 MB) into `.sandbox/media` with ffmpeg (`testsrc2` video and sine audio;
default `C:\ProgramData\Jellyfin\Server\ffmpeg.exe`). Re-running it adds only missing files (`-Force` regenerates), so
a sandbox made before a file was added gets it at its next scan.

- Movies and shows for the identity and duplicate checks: a duplicate across Movies and Movies 2, remake pairs
  (different and equal `[tmdbid-]`), a folder-named unidentified item, a two-version movie, a `[tmdbid-]` folder, a
  60 s / 90 s edition pair, folder vs mixed-folder pairs, a diacritics pair, a two-season `[tvdbid-]` series, two home
  videos, and "Chaptered Movie (2022)" with embedded chapters (named, a ripper's "Chapter 4", an untitled one) for the
  name dialog's suggestion.
- `Music/`: two artists with one album each, two tagged 12 s FLAC tracks per album.
- `LM Duplicates/` and `LM Duplicates 2/` for Library Maintenance ("LM Confirmed Pair (2010)" in both with the same
  ids in their NFOs; "LM Edition Pair (2003)" and its " - Extended" edition), so the `default` counts other checks use
  don't change.
- `Player Tracks\Multi Track (2021)\Multi Track (2021).mkv`: 30 s with two audio tracks (English, French) and two SRT
  subtitle tracks, for a player check with more than one track. No library covers it unless
  `Initialize-Sandbox.ps1 -PlayerTracks`; remove that library afterwards (Dashboard, Libraries, or
  `DELETE /Library/VirtualFolders?name=Player%20Tracks&refreshLibrary=true`).
- The scale seed (`-SyntheticMovies`, `-SyntheticEpisodes`, `-SyntheticSeed`) creates `Synthetic/` movie folders
  (about 5 % planted duplicates and variants) and a `SyntheticTV/` library of up to 200 shows; `HardLink` links one
  2 s clip everywhere (a new link source every 1000 links), `Stub` writes zero-byte files.

### Set-SandboxPluginConfig.ps1

```powershell
./scripts/Set-SandboxPluginConfig.ps1 -Name <name> -Plugin Bookmarks [-Set @{ EnablePageDemo = $true; PageHostMode = 'Auto' }]
```

Reads a plugin's configuration (`GET /Plugins/<guid>/Configuration`, the GUID from its `build.yaml`) in a running
sandbox, changes the given settings, posts the whole configuration back and prints the changed settings as read back;
without `-Set` it prints the configuration. Only the sandbox's loopback URL is called, with the admin token from
`credentials.json` (never printed). Settings read at startup (`PageHostMode`, `RegisterPluginPages`) need a restart:
`Stop-Sandbox.ps1`, then `dev-server.ps1 -Name <name> -Background -NoBuild`.

### JellyfinSandbox.psm1

The shared module, not run directly: the path guards (`Test-ProtectedPath`, `ConvertTo-FileSystemPath`,
`Test-RealServerTarget`), the clip-path check (`Test-SandboxClipPaths`), the download-task table and its check
(`Test-SandboxTasksOff`), the server environment (`Get-SandboxServerEnvironment`, `Invoke-WithChildEnvironment`), the
process start and stop helpers (`Start-ProcessWithLogs`, `Wait-ProcessExit`), the release checks
(`Assert-PluginReleaseCheckout`, `Assert-BuildIsCompilerOutput`, `Get-RepoScriptsProblem`) and git calls in their own
processes (`Invoke-Git`). Repo.Tests' `SandboxToolingTests`, `ScriptsTests` and `ReleaseInstallTests` cover it.

## webcheck.mjs

A headless browser check using only Node 22 built-ins and the Chrome DevTools Protocol. It launches Chrome (or Edge)
with `--headless=new`, a temporary profile under `.sandbox/<name>/browser` and `--remote-debugging-port=0`, runs the
steps, prints one JSON result and always closes the browser it started (`Browser.close`, then that PID, then any helper
process whose command line names the profile). The profile is deleted unless `--keep-profile`.

```powershell
node scripts/webcheck.mjs --sandbox default --steps bookmarks-details.json --out result.json
node scripts/webcheck.mjs --sandbox default --login viewer --goto "/web/index.html#/home" --wait "!!document.querySelector('.homePage:not(.hide)')" --screenshot home.png
node scripts/webcheck.mjs --help
```

| Option | Effect |
| --- | --- |
| `--sandbox <name>` | the sandbox (default `default`); URL from its `sandbox.json` |
| `--url <url>` | a base URL instead (loopback only, never the real server's port 8096) |
| `--steps <file>` | the steps file (relative paths: the current folder, then `scripts/webchecks/`) |
| `--browser chrome\|edge` | default chrome, falling back to edge |
| `--headed` | show the browser window |
| `--timeout <ms>` | default step timeout (20000) |
| `--fail-on-exception` | fail on an uncaught page exception (except jellyfin-web's known noise) |
| `--allow-external` | let the page reach non-loopback hosts and JE's CDN proxy route |
| `--keep-profile`, `--out <file>` | keep the browser profile; also write the result to a file |
| `--login`, `--goto`, `--wait`, `--eval`, `--assert`, `--click`, `--type`, `--key`, `--screenshot`, `--sleep` | steps as flags, run in the order given after any `--steps` file |

**Steps files.** A JSON object with a `name` or `description` (what it checks and needs) and a `steps` list, one
action per object. Any step may carry
`name`, `timeout`, `save`, `optional` (a failure is recorded but the run goes on), `full` (record the whole value, not
a 500-character preview), `if` (`"<js>"`: run only while it is truthy in the page, otherwise recorded as passed with
`"skipped": true`) and `finally` (run even after an earlier step failed, for putting back what a webcheck changed).

| Step | Does |
| --- | --- |
| `{"login": "admin"}` | Signs in through the login form with the password from `credentials.json` (never printed); `"stay": true` uses the login page already shown (after a signed-out deep link). Retypes when jellyfin-web re-renders the form; empties the fields before a failure screenshot. |
| `{"goto": "/web/index.html#/home"}` | Navigates (paths relative to the base URL, BaseUrl included); waits for the load event unless only the hash changes. |
| `{"waitFor": "<js>"}` | Polls the expression (it may `await`) until truthy. |
| `{"eval": "<js>", "save": "x"}` / `{"script": "<statements with return>"}` | Evaluates; `save` keeps the value for `${x}` in later strings and `__vars.x` in page code. |
| `{"assert": "<js>", "message": "..."}` | Fails unless truthy. |
| `{"click": "<selector>"}` | Real mouse click on the element's centre (`"js": true`: `element.click()`). |
| `{"type": "text", "selector": "#x", "clear": true}` | Inserts text into the focused (or given) element. |
| `{"key": "Shift+B"}` | A key press with modifiers (Shift, Ctrl, Alt, Meta) or a named key (Enter, Escape, ArrowLeft, F1); `{"key": "Shift"}` presses the modifier alone. |
| `{"screenshot": "name.png"}` | PNG under `.sandbox/<name>/browser/screenshots/` (or an absolute path). |
| `{"sleep": 500}` / `{"reload": true}` | Waits / reloads. |
| `{"noExceptions": true, "ignore": "<regex>"}` | Fails if the page threw an uncaught exception; `ignore` skips matching messages or script URLs. |
| `{"initScript": {"file": "probe.js"}}` or `{"initScript": "<js>"}` | Runs the script in every later document before any page script, from the next `goto`, `reload` or `login`. A relative `file` resolves in the steps file's folder, then the current folder. The helpers in `webchecks/probes/` are such scripts. |
| `{"delayRequests": {"url": "/Bookmarks/ClientScript", "ms": 1500}}` | Holds later requests whose URL contains `url` for `ms` (at most 20000); `null` stops. Listed under `delayedRequests`. |
| `{"emulate": {"width": 390, "height": 844, "mobile": true, "touch": true}}`, `{"emulate": {"media": {"forced-colors": "active"}}}`, `{"emulate": "desktop"}` | Screen (also `deviceScaleFactor`), touch (`(pointer: coarse)` matches) and media features; unnamed media features are cleared; `"desktop"` is 1366x900. Set the screen before `login`. |
| `{"context": "<name>"}` | Runs the following steps in another page (`main` is the first); a new name gets its own browser context, signed out until a `login`: another window or device. Same request policy; `__vars` shared. |
| `{"cdp": "Input.dispatchTouchEvent", "params": {...}}` | One raw DevTools command, `Input` domain only; `params` may be a page expression. Touch checks use `dispatchTouchEvent` sequences (they scroll and honour `touch-action`). |

**The result** lists every step (`ok`, `ms`, `value` or `error`), console messages, uncaught exceptions, failed requests
(HTTP ≥ 400 and network failures), blocked and delayed requests and screenshots. A failing step stops the run and saves
a `failure-*.png`. Exit code 0 means every step passed (and, with `--fail-on-exception`, no uncaught exception but the
known noise).

**Request policy** (`sandbox-policy.mjs`, tested by `scripts/tests/sandbox-policy.test.mjs`): requests to anything but
loopback, and always to the real server's port (`PRODUCTION_PORTS`: 8096), are blocked unless `--allow-external`; so are loopback requests to JE's CDN proxy
`/JellyfinEnhanced/cdn/…` (any case, after any BaseUrl or `/emby` prefix), which would make the sandbox download.

**Redaction** (`redact.mjs`, tested by `scripts/tests/redact.test.mjs`): the printed and saved result has every
session token and API key replaced by `REDACTED`: `api_key=`, `ApiKey=` and `access_token=` in URLs, `X-Emby-Token`
and `X-MediaBrowser-Token` headers, the authorization header's `Token="…"`, and `"AccessToken"`/`"api_key"`
properties, also escaped inside a JSON string. Ids are kept. A script that saves other output must keep tokens out
itself.

**jellyfin-web's known noise** (`page-noise.mjs`, tested by `scripts/tests/page-noise.test.mjs`): exceptions
jellyfin-web throws in a headless browser whatever the page does are kept in `exceptions` with `"known": "<what it
is>"`, and neither `noExceptions` nor `--fail-on-exception` counts them. It holds one: 10.11 answers a
`LibraryChanged` event with a "New Movie" notification through the service worker without checking the permission,
so `TypeError: Failed to execute 'showNotification' …` is thrown whenever a scan adds an item while a page is open.
Exceptions that belong to one page stay in the `ignore` of the webchecks that meet them.

Other expected noise, not caused by our plugins:

- `GET /CustomRatings/script` 404: the installed `index.html`, and so every sandbox's copy, carries a stale
  `<script plugin="CustomRatings">` tag. With a BaseUrl it is answered with a 302 to `<BaseUrl>/web/` and reported as
  `SyntaxError: Unexpected token '<'`; use `"ignore": "/CustomRatings/script"` on `noExceptions` there.
- `GET /web/null` 404 on a details page: the image card of an item without images (image fetchers are off).
- The blocked Chromecast sender (`https://www.gstatic.com/cv/js/sender/v1/cast_sender.js`).
- With JE, blocked `/JellyfinEnhanced/cdn/…` requests, with a `net::ERR_BLOCKED_BY_CLIENT` console line each.
- `Error: player cannot be null` from `main.jellyfin.bundle.js` when a webcheck leaves the video player by navigating
  away (`bookmarks-ui.json` ignores it).

**The script tests.** `scripts/tests/*.test.mjs` test the runner's helpers and a few probes without a server
(`sandbox-policy`, `redact`, `page-noise`, `api-docs-probe` against a fake server, `bookmarks-requests-probe`,
`dev-server-launch`). Run one with `node --test scripts/tests/<name>.test.mjs`; Repo.Tests' `ScriptNodeTests_Pass`
runs them all.

## Kit and web hosting checks

The shared kit, checked through Bookmarks (its reference plugin) and its developer demos. A demo is an admin-only
Bookmarks feature behind its own setting, off by default and not on the settings page; the probe turns it on and back
off, and checks that it answers 403 `urn:jellyfin-plugin-kit:feature-disabled` while off.

### index-probe.mjs

Raw HTTP checks of the kit's `index.html` injection (the admin token flips `EnableWebUi`).

```powershell
node scripts/index-probe.mjs --sandbox default [--plugin Bookmarks] [--fault] [--timing 10] [--wait 60] [--out probe.json]
```

- `--plugin <Name>`: only this plugin (repeatable; default `sandbox.json` `Plugins`).
- `--fault`: the invalid-UTF-8 check (sandboxes without File Transformation only).
- `--timing <n>`: GETs timed with the web UI on and off (default 10; 0 skips).
- `--wait <s>`: wait for File Transformation's late tags (default 60).

Covers: each plugin's tag once and uncached across URL variants (legacy prefixes, BaseUrl, `Range`), an anonymous
cached `ClientScript`, other responses still compressed, `EnableWebUi=false` removing only that tag, community tags once
each, Bookmarks 1.0's `data-bookmarks-plugin` absent. `--fault` corrupts the sandbox's copy of `index.html` and restores
it at once; it must be served byte for byte. Browser counterparts: `webchecks/bookmarks-injection.json`,
`bookmarks-injection-off.json` and `bookmarks-details.json` (pass in `default`, `bare`, `baseurl` and `je`).

### api-docs-probe.mjs

`GET /api-docs/openapi.json` must answer 200: one schema-id collision among the loaded plugins makes the whole document
500, silently. `Initialize-Sandbox.ps1` runs it with `--summary`; run it after every Jellyfin or Jellyfin Enhanced update
(JE can update itself and publishes many unprefixed ids), next to `Test-WebAnchors.ps1`.

```powershell
node scripts/api-docs-probe.mjs --sandbox <name> [--summary] [--out result.json] [--inventory-out ids.json]
node scripts/api-docs-probe.mjs --url http://127.0.0.1:<port>[/<BaseUrl>] --log-dir <dir> [--plugins Bookmarks,...]
```

- `--sandbox <name>`, or `--url <loopback url>` with `--log-dir <dir>` (the server log it reads).
- `--plugins A,B`: our plugins (default `sandbox.json` `Plugins`).
- `--schema-prefix A=Prefix,...`: overrides `KitOptions.SchemaPrefix` read from the plugin's source.
- `--foreign-ids <file>`: ids no plugin may use (default `kit/testdata/openapi-schema-ids.txt`).
- `--timeout <s>` (180), `--request-timeout <s>` (120), `--retry-delay <ms>` (2000).
- `--summary`: one line; `--inventory-out <file>`: paths and schema ids, to refresh `openapi-schema-ids.txt`.

Requests go one at a time; the 10-13 s first generation's 500 "OpenApi document is generating" is retried. A
`SwaggerGeneratorException` or `Can't use schemaId` in the log is a **collision**, reported with its log lines, not
retried. Each plugin needs a path under `/<Route>`, and its own schemas must carry its prefix and not be foreign ids
(which a sandbox without the publishing plugin wouldn't show). `scripts/tests/api-docs-probe.test.mjs` tests it.

### Web kit webchecks

| Steps file | Checks |
| --- | --- |
| `bookmarks-ui.json` | Bookmarks' web UI end to end (resets the admin's bookmarks on Long Test Movie, sandbox data only), with DOM snapshots (`probes/dom-snapshot.js`) |
| `bookmarks-phone.json` | a 390x844 touch screen (`bookmarks-phone-*.png`): bottom sheets, touch-size buttons, `forced-colors` |
| `kit-boot.json` | `ApiClient` seen within 50 ms, no plugin request before the user (`probes/kit-boot-probe.js`) |
| `bookmarks-theme.json` | four themes' native colours and accent (`probes/kit-theme-probe.js`) |

**`compare-snapshots.mjs`** compares two webcheck results' snapshots, for "the UI is unchanged":

```powershell
node scripts/compare-snapshots.mjs before.json after.json [--ignore-classes a,b] [--ignore-attrs x,y] [--out diff.json]
```

The ignore options drop intended changes on both sides; exit 0 only when all match (2 on bad arguments). Compare a new
build with the build before it.

### Page hosting webchecks

Checked on Bookmarks' page demo (`#/jfbm-demo-a`, `#/jfbm-demo-b`), present only while `EnablePageDemo` is on.
`EnableDemoPageB` turns B off; `PageHostMode` (`FallbackOnly` default, `Auto`, `PluginPagesOnly`) and
`RegisterPluginPages` choose the host at startup; `ShowFallbackNav` the "Library tools" links. The files are generated by
`webchecks/gen-pages.mjs` (Repo.Tests fails when they differ): edit the generator.

| Steps file | Checks | Sandbox, mode |
| --- | --- | --- |
| `pages-fallback.json` | the fallback host's navigation and hand-back, 0 flash frames | `bare`, `default`, `baseurl`; `FallbackOnly` |
| `pages-signed-out.json` | a signed-out deep link | any; demo on |
| `pages-slow-script.json` | a cold load with a slow `ClientScript` | any; demo on |
| `pages-pluginpages.json` | the pages in Plugin Pages' host | `default`, `baseurl`; `Auto` at startup |
| `pages-relogin.json` | sign out and in without a reload | any; demo on |
| `pages-off.json` | page B and the web UI turned off | `default`, `baseurl` |
| `pages-off-bare.json` | the same without Plugin Pages | `bare`; demo on |

`pages-fallback`, `-slow-script`, `-off` and `-off-bare` turn the demo on themselves (the `-off` ones restore page B and
the web UI); the others need it on. `probes/pages-probe.js` counts flash frames; `probes/pages-prehide-off.js` is the
positive control.

```powershell
./scripts/Set-SandboxPluginConfig.ps1 -Name bare -Plugin Bookmarks -Set @{ EnablePageDemo = $true }   # bare running
node scripts/webcheck.mjs --sandbox bare --steps pages-fallback.json   # then pages-signed-out, pages-slow-script
./scripts/Set-SandboxPluginConfig.ps1 -Name default -Plugin Bookmarks -Set @{ EnablePageDemo = $true; PageHostMode = 'Auto' }
./scripts/Stop-Sandbox.ps1 -Name default; ./scripts/dev-server.ps1 -Name default -Background -NoBuild   # registration is at startup
node scripts/webcheck.mjs --sandbox default --steps pages-pluginpages.json   # then pages-relogin, pages-off
# afterwards set EnablePageDemo = $false (and PageHostMode = 'FallbackOnly') again
```

The first session after a restart into `Auto` can find the drawer empty; `pages-pluginpages.json` reloads once after
the sign-in to avoid it.

### Test-WebAnchors.ps1

Checks that the jellyfin-web anchors we rely on (`kit/web/anchors.json`, each plugin's `Web/anchors.json`) still exist
in a web client bundle. Read only; `-Out` is refused under `C:\ProgramData\Jellyfin`. Run it after every Jellyfin,
Jellyfin Enhanced or Plugin Pages update and before `Install-RealServer.ps1`.

```powershell
./scripts/Test-WebAnchors.ps1 [-Sandbox default | -WebDir <jellyfin-web folder> | -JellyfinDir <dir>] [-Plugin Bookmarks] [-Strict] [-RequirePluginPages] [-Out result.json]
```

- `-JellyfinDir` (default `C:\ProgramData\Jellyfin\Server`), `-Sandbox <name>` or `-WebDir <dir>`: the bundle to read.
- `-Plugin <Name[]>`: only these plugins' anchors.
- `-Strict`: every anchor `ok` and Plugin Pages' anchors as expected; `-RequirePluginPages`: the latter only.
- `-Out <file>`: the JSON result.

An anchor is `ok` when its primary selector's names occur in the bundle, `fallback` when only a fallback's do, `missing`
otherwise. **Plugin Pages' three bundle anchors** (the build-specific text its File Transformation patches need) only
warn unless `-RequirePluginPages` or `-Strict`. Exit 0 when the required anchors hold, 1 otherwise, 2 when the bundle or
an anchor list can't be read. Repo.Tests `WebAnchorsScriptTests` tests it.

### Web anchor and health report webchecks

The health self-check, on a **developer build** of Bookmarks that renames one anchor (`-p:KitRenameAnchors`,
`kit/build/Kit.props`; never install it on a real server). `probes/health-probe.js` records warnings and ClientHealth
requests.

| Steps file | Checks | Build, user |
| --- | --- | --- |
| `health-missing-anchor.json` | one warning and one rate-limited report; forged, flooding and oversized reports dropped | renamed, `viewer` |
| `health-native.json` | a native wrapper's report | renamed, `viewer2` |
| `health-diagnostics.json` | Diagnostics keeps only the genuine reports | renamed, `admin`, after the two above on a fresh process |
| `health-quiet.json` | the normal build reports nothing | normal, `admin` |

```powershell
dotnet publish plugins/Bookmarks/Jellyfin.Plugin.Bookmarks -c Debug -o artifacts/deploy/Bookmarks/Debug -p:JellyfinTarget=10.11 -p:KitRenameAnchors=detailButtons
./scripts/dev-server.ps1 -Name default -Background -NoBuild   # default stopped first
node scripts/webcheck.mjs --sandbox default --steps health-missing-anchor.json   # then health-native, health-diagnostics
./scripts/Stop-Sandbox.ps1 -Name default; ./scripts/dev-server.ps1 -Name default -Background   # a normal build again
node scripts/webcheck.mjs --sandbox default --steps health-quiet.json
```

### Bookmarks on the kit without File Transformation

The kit's checks in `default`, `bare` and `baseurl`, one sandbox at a time: the webchecks above plus these.

| Check | What it shows | Where |
| --- | --- | --- |
| `bookmarks-api-probe.mjs` | Bookmarks' REST API and clips (see "Bookmarks checks") | any |
| `webchecks/store-banner.json` | a read-only `bookmarks.json`: the warning banner and Diagnostics (`store-banner-top.png`) | `default`, read-only fixture |
| `webchecks/kit-registry.json` | every plugin on one kit version; no console errors (`probes/console-probe.js`) | any |
| `webchecks/users-viewer.json` | switching users without a reload; `viewer` sees no admin UI | any |
| `webchecks/layouts.json` | the TV, mobile and experimental layouts (`../kit/README.md`, "Page hosting") | any |

### Identity probe

`identity-probe.mjs` checks the kit's item identity (`../kit/README.md`, "Item identity") against a real library
through Bookmarks' identity demo (`EnableIdentityDemo`).

```powershell
node scripts/identity-probe.mjs --sandbox default [--timeout 600] [--out .sandbox/default/results-identity/identity-probe.json]
```

`--timeout <s>`: per library scan. Needs a running sandbox with the standard media (`New-SandboxMedia.ps1`) and
libraries (`Initialize-Sandbox.ps1`), and no other sandbox up. It renames folders only inside `.sandbox/media` and
`.sandbox/<name>/identity-probe/held`, journals each move in `.sandbox/<name>/identity-probe/moves.json` first, and
undoes them in reverse when a scenario ends, on failure, and at the next run after a killed one (then scans once). Its
synthetic `<data>/librarymaintenance/moves.jsonl` is deleted at the end. Covers resolution, relinks and the move journal.

### Library changes probe

`library-changes-probe.mjs` checks the kit's library-change monitor (`../kit/README.md`, "Background work and library
changes") through Bookmarks' library change demo (`EnableLibraryChangeDemo`).

```powershell
node scripts/library-changes-probe.mjs --sandbox default [--timeout 600] [--out .sandbox/default/results-library-changes/library-changes-probe.json]
```

`--timeout <s>`: per scan. Needs a running sandbox with the standard media and libraries. It adds two probe folders
under `Movies/` and `Shows/`, records them in `.sandbox/<name>/library-changes-probe/created.json` before writing, and
removes exactly those at the end, on failure, and at the next run after a killed one. It restarts nothing. Covers one
full pass per scan, Jellyfin's post-scan changes in a later batch, and the timer path.

### Users probe

`users-probe.mjs` checks the kit's users and data services (`../kit/README.md`, "Users and data") through Bookmarks'
user data demo (`EnableUserDataDemo`).

```powershell
./scripts/dev-server.ps1 -Name default -Background         # deploys the current build; the probe restarts with -NoBuild
node scripts/users-probe.mjs --sandbox default [--skip-webcheck] [--out .sandbox/default/results-users/users-probe.json]
```

`--skip-webcheck`: skip `webchecks/users-banner.json`. It restarts the sandbox seven times (output in
`.sandbox/<name>/users-probe/script-output-<n>.log`; see "dev-server.ps1 and Stop-Sandbox.ps1"), changes only the demo's
files in the Bookmarks data folder, refuses folders outside `.sandbox`, and leaves the sandbox running. Its two
throwaway users get random, unstored passwords and are deleted. Covers deleted users, export and import, snapshots,
newer-version and restored stores, and the users banner.

### Library demo probe

`library-demo-probe.mjs` checks the kit's record lifecycle and library reads (`../kit/README.md`, "Record lifecycle
and library reads") through Bookmarks' library demo (`EnableLibraryDemo`) and its "Bookmarks: Maintenance" task.

```powershell
./scripts/dev-server.ps1 -Name default -Background         # deploys the current (Debug) build; the probe restarts with -NoBuild
node scripts/library-demo-probe.mjs --sandbox default [--timeout 600] [--skip-restart] [--out .sandbox/default/results-library-demo/library-demo-probe.json]
```

`--timeout <s>`: per scan; `--skip-restart`: skip the startup-sweep restart. Needs the standard media and libraries and
a **Debug** build. Media moves work as in the identity probe, under `.sandbox/<name>/library-demo-probe/` (`held`,
`moves.json`, `created.json`). It also changes and restores `viewer`'s user data, `viewer2`'s library access and merged
versions, and empties the demo's records at start and end. Covers detach, reattach and relink, the verify sweeps,
maintenance, retention, purge, fresh user-data reads, `LibraryAccess` and `VersionSets`.
## Bookmarks checks

Each probe checks Bookmarks in a running sandbox and names the section of `plugins/Bookmarks/DEVELOPMENT.md` it covers.
Unless a section says otherwise: deploy first with `./scripts/dev-server.ps1 -Name default -Background` (Debug); the
standard media, libraries and users (`viewer` without download permission, `viewer2` with it, not an admin); every name
and note a probe gives starts with its prefix and lists are searched for it, so leftovers of other runs don't count; the
last check is no Error of Bookmarks or the kit in the log and no line at Information or above naming a bookmark, note
or clip folder. A probe works in `.sandbox/<name>/<probe name>/`, deletes its bookmarks and clips through the API,
never deploys, writes `bookmarks.json` only where said (stopped), and restarts only through `Stop-Sandbox.ps1` and
`dev-server.ps1 -Background -NoBuild`. "Unplugged" means a folder renamed aside; a file "held open" is held by a
PowerShell process of the probe's (read, sharing read and write, not delete), as a download holds it.

### bookmarks-api-probe.mjs

Bookmarks' REST API and clips end to end ("REST API"), in any sandbox; also the kit's checks without File
Transformation (`bare`, `baseurl`).

```powershell
node scripts/bookmarks-api-probe.mjs --sandbox default [--movie "Long Test Movie"] [--keep-files] [--restart] [--names] [--fixture <file> [--expect-failed-upgrade]] [--timeout 180] [--out <file>]
```

- `--movie <name>` (default `Long Test Movie`); `--timeout <s>` (180).
- `--keep-files`: keep downloads and script logs under `.sandbox/<name>/api-probe`.
- `--restart`: also restart and check the next start's clean-ups; normal run and read-only fixtures only.
- `--names`: only the name checks and the log check; not with `--fixture` or `--restart`.
- `--fixture <file>` [`--expect-failed-upgrade`]: an installed old or read-only store (see "The 1.0.0 store").

The normal run covers CRUD, Fast and Accurate clips (downloaded, streamed, `ffprobe`d), access rules (a temporary API
key, never printed), tombstones, a held clip file, an offline clip folder and log privacy. For the offline folder it
renames the clip folder to `<folder>.api-probe-unplugged` and back in a `finally`; **a killed run leaves it aside, and
the next normal run renames it back first**. `--restart` adds pending deletes, the stale `.partial` janitor (it writes
hour-old files and removes the ones that stay) and queued jobs resuming. The name checks (`evidence.names`) cover
normalisation, limits, `IfUpdatedAt` conflicts, replace semantics and named clip files. `webchecks/names-editor.json`
(admin): the editor keeps a name and shows another tab's rename as a 409 with Reload.

#### The 1.0.0 store

`--fixture` checks an old store being upgraded, or read-only data:

1. Stop the sandbox and move `data/data/bookmarks/bookmarks.json*` aside.
2. Copy `plugins/Bookmarks/tests/Jellyfin.Plugin.Bookmarks.Tests/Fixtures/bookmarks-1.0.0.json` (written in the
   `default` sandbox, whose user ids it holds) there as `bookmarks.json`.
3. Its clip paths name the `default` sandbox of a checkout at `C:\src\jellyfin-plugins`. Elsewhere, replace the prefix
   `C:\src\jellyfin-plugins\.sandbox\default\` in the copy with your checkout's `.sandbox\default\` (each `\` doubled in
   JSON), as Repo.Tests' `SandboxClipPathTests` does, and pass that copy as `--fixture`; otherwise `dev-server.ps1`
   refuses to start.
4. Start the sandbox, run `node scripts/bookmarks-api-probe.mjs --sandbox default --fixture <that file>`, and again
   after a restart.
5. Move the original files back.

**A schema 1 file from anywhere else, a real server's above all, needs every `FilePath` rewritten into
`.sandbox\default\data\data\bookmarks\clips\…` first**: the upgrade records each clip folder in `ClipRoots`, and the
first start would write clips and delete stale `.partial` files there, unattended. `dev-server.ps1` refuses to start
while any clip path lies outside the sandbox or under `C:\ProgramData\Jellyfin`, and lists them.

| Fixture | Checks |
| --- | --- |
| `bookmarks-1.0.0.json` | the upgrade to schema 2: one `bookmarks.json.v1-<yyyyMMddHHmmss>` copy equal to the fixture, `Migrated` (first start) or `Ok`, the 1.0 values plus identity, each user's list |
| `bookmarks-schema3.json` | a newer version's file: `ReadOnlyNewerVersion`; with `--restart` the janitor skips its clip folder |
| `bookmarks-1.0-duplicate-key.json` | with `--expect-failed-upgrade`: `ReadOnlyMigrationFailed`, the `.v1-*` copy kept |
| `bookmarks-1.0-dev-sandbox.json`, `bookmarks-schema2.json` | unit tests only; `dev-server.ps1` refuses them |

Read-only data means `StoreReadOnly` in the Capabilities, the clip worker idle, every write 503, clip files still served,
the files unchanged. The janitor's read-only gate is unit-tested (`Clips/PartialFileJanitorTests`).

### clip-guard-probe.mjs

The free-space guard ("Free space"), with `MinFreeSpaceMegabytes` raised above C:'s free space instead of a full disk.

```powershell
node scripts/clip-guard-probe.mjs --sandbox default [--movie "Long Test Movie"] [--skip-webcheck] [--timeout 180] [--out <file>]
```

`--skip-webcheck` leaves out the webchecks; `--timeout` seconds per clip (180). It covers refusals (admin's and
`viewer2`'s wording), a folder not made yet or on a missing drive, the check at a job's start and the watchdog stopping a
running clip. It changes `MinFreeSpaceMegabytes`, `ClipOutputPath` and `VideoPreset`, put back in a `finally`. **A run
killed while `ClipOutputPath` names the missing drive leaves it so, and `dev-server.ps1` refuses to start until it is
put back.** `webchecks/clip-guard-settings.json` (admin): the free-space line and warnings on the settings page;
`webchecks/clip-guard-refused.json` (`viewer2`): the refusal as a toast.

### bookmarks-identity-probe.mjs

The identity service ("Missing and moved videos"): bookmarks detaching, reattaching and relinking as the shared media
moves, the move journal and backfill at a restart, and the daily maintenance.

```powershell
node scripts/bookmarks-identity-probe.mjs --sandbox default [--timeout 600] [--skip-restart] [--out <file>]
```

`--timeout` seconds per scan (600); `--skip-restart` leaves out the restart. It renames only inside `.sandbox/media`
and its `held/`. While stopped it moves `Blade Runner (1982)` as Library Maintenance would, writes
`data/data/librarymaintenance/moves.jsonl` (an existing one set aside and put back), and edits two bookmarks in
`bookmarks.json` into the 1.0-upgrade shape.

### bookmarks-query-probe.mjs

The My Bookmarks page's endpoints (`Query`, `Groups`, `BulkDelete`, preferences, the OpenAPI document) through the API
("REST API").

```powershell
node scripts/bookmarks-query-probe.mjs --sandbox default [--timeout 300] [--out <file>]
```

`--timeout` seconds per scan (300). It holds `Movies/Big Buck Test (2008)` out for one scan, takes Movies 2 from
`viewer2` (its whole policy written back at once and in a `finally`) and puts `viewer`'s naming preferences back.

### bookmarks-rows-probe.mjs

The editor, rows, "My bookmarks" section and delete confirmation in the web client ("Using it").

```powershell
node scripts/bookmarks-rows-probe.mjs --sandbox default [--skip-relink] [--timeout 300] [--out <file>]
```

`webchecks/editor-rows.json` (admin) checks naming, the editor and the delete confirmation; `webchecks/rows-relinked.json`
(viewer) the relinked badge and Looks right, after the probe holds `Same Id Remake (1999)` out and scans. `--skip-relink`
runs only the first; `--timeout` seconds per scan (300).

### Jellyfin Enhanced interop

The kit's JE interop (`kit/README.md`, "Jellyfin Enhanced interop") in the `je` sandbox (the newest JE of the real
installation, the pinned fixture). JE behaviour the checks rely on: its Bookmarks switched off takes effect at the next
page load and leaves its `B` key active; its Plugin Pages link goes at the next restart (or the second); a cleared key
comes back as `B` at its next start, so "Use B" waits for a lasting state; any other key or `Disabled` frees `B` for
good.

```powershell
node scripts/webcheck.mjs --sandbox je --steps je-shortcuts.json
node scripts/webcheck.mjs --sandbox je --steps je-settings.json
node scripts/je-switchoff-probe.mjs --sandbox je [--skip-webchecks] [--out <file>]
```

- `webchecks/je-shortcuts.json`: our `Shift+B` next to JE's `B`, each handled once.
- `webchecks/je-settings.json`: the banner, reserved-key warning, switch-off checklist and "Use B" as Diagnostics says,
  in every state the probe leaves.
- `webchecks/je-pages-order.json` (read-only, never JE's Save): JE's four Pages boxes untick in the checklist's order.
- `je-switchoff-probe.mjs` walks the checklist through JE's and Custom Tabs' APIs with restarts, for bookmarks and
  reviews (a synthetic per-user `shortcuts.json` is deleted after). In a `finally` it puts the three plugins' settings
  back, restarts (twice if the link needs it) and runs `Initialize-Sandbox.ps1 -Name je`.

### bookmarks-player-probe.mjs

The player's naming flow ("While watching") as `viewer` on "Chaptered Movie (2022)" (`New-SandboxMedia.ps1` makes it;
the probe scans once if needed).

```powershell
node scripts/bookmarks-player-probe.mjs --sandbox default [--skip-buttons-off] [--out <file>]
```

It sets `ShowPlayerButtons`, `AskForNameDefault` and `SuggestChapterNames` and viewer's preferences, put back in a
`finally`. `webchecks/player-naming.json`: bookmarking and the name dialog while playing;
`webchecks/player-buttons-off.json` (left out by `--skip-buttons-off`): `Shift+B` without our buttons.

### bookmarks-page-probe.mjs

The My Bookmarks page ("The My Bookmarks page") in `default`, `bare` (fallback route) and `baseurl` (`/jf`), plus its
shell and `index.html` fragment over raw HTTP.

```powershell
node scripts/bookmarks-page-probe.mjs --sandbox default [--skip-off] [--skip-layouts] [--timeout 180] [--out <file>]
```

`--skip-off` leaves out `page-bookmarks-off.json`, `--skip-layouts` `page-tv.json` and `page-phone.json`; `--timeout`
seconds per scan (at least 10). For **Not in library** it holds `Tagged Movie (2012)`'s folder out and scans (removing a
library path with `DELETE /Library/VirtualFolders/Paths` removes no item). It changes `EnableBookmarksPage` and
viewer's policy and preferences, put back in its `finally`. `page-bookmarks.json` and `page-actions.json` clear and
widen the browser's resource timing buffer (250 entries) first, or later requests go unrecorded.

| Webcheck | What it checks |
| --- | --- |
| `page-bookmarks.json` (admin) | views, sorts, filters, search, paging and history; also runs alone |
| `page-bookmarks-off.json` (admin) | the page turned off |
| `page-unavailable.json` (admin, viewer) | a video in a library viewer lost shows Unavailable |
| `page-actions.json` (admin) | rename, selection and bulk delete, also stopped part way |
| `page-actions-inuse.json` (admin) | a bulk delete with a held clip file |
| `page-player-options.json` (viewer) | Player options and the per-device shortcut |
| `page-tv.json`, `page-phone.json` (admin) | the TV and phone layouts |
| `page-bookmarks-pluginpages.json` (admin, by hand) | the Plugin Pages host (`PageHostMode=Auto`, after a restart) |

`page-bookmarks-pluginpages.json` waits for Plugin Pages' tag in `index.html`; right after a restart its first run can
still fail the drawer step, and a second run passes.

### Bookmarks JE import probe

The import of JE's bookmarks ("Jellyfin Enhanced: moving its bookmarks over") in `je`, then the whole switch-off
checklist, with the synthetic fixture `kit/testdata/je-data/import-81` (never real JE data): three users' bookmark files
covering each match and skip kind; the expected counts follow from it (the probe's `EXPECTED`). Deploy with
`dev-server.ps1 -Name je -Background` and run `Initialize-Sandbox.ps1 -Name je` first (it scans the fixture's media).

```powershell
node scripts/bookmarks-je-import-probe.mjs --sandbox je [--skip-webchecks] [--out <file>]
```

It writes the `bookmark.json` files into JE's data folder with the ids mapped to the sandbox's videos (never over a file
it didn't write) and covers the dry run, the import (`webchecks/je-import-ui.json`), re-runs, the checklist with
restarts and the page (`webchecks/je-import-page.json`). In a `finally` it deletes the JE files, puts the settings back,
restores `bookmarks.json` and its `.bak` while stopped, deletes the `bookmarks-import-*` snapshots and runs
`Initialize-Sandbox.ps1 -Name je`; the sandbox is left running. JE 12.11 can't read the fixture's malformed entry (a
text `Timestamp`): it logs an Error and shows that user no bookmarks; Bookmarks' import reports the one entry.

### bookmarks-settings-probe.mjs

The settings page and admin tools ("Settings", "Backups: export, import, snapshots") in `default` or `je` (there also
`ShowJellyfinEnhancedBanner`).

```powershell
node scripts/bookmarks-settings-probe.mjs --sandbox default [--movie "Long Test Movie"] [--skip-webcheck] [--timeout 180] [--out <file>]
```

It covers the counts, shortcut check, export, import and restore (refused while a clip runs). It deletes its bookmark
and puts the whole configuration back; the snapshot it restores is undone by restoring the restore's own snapshot.
Webchecks: `settings-roundtrip.json` (every setting), `settings-data.json` (the Data tab; a confirmation waits for
jellyfin-web's dialog to stop animating), `settings-phone.json` (375x812). The page has tabs: `je-import-ui.json` and
`je-settings.json` open Data first; `health-diagnostics.json`, `store-banner.json` and `users-banner.json` Diagnostics.

### Bookmarks full sandbox run

Bookmarks' whole checklist in `default`, `bare`, `baseurl` and `je`, one at a time: every probe and webcheck of this
family where it applies, the checks without File Transformation, the upgrade from 1.0.0 and back, and
`Install-RealServer.ps1` on a stopped sandbox. `bookmarks-data-probe.mjs`, `bookmarks-je-web-probe.mjs` and
`bookmarks-upgrade-probe.mjs` (below) cover what the others don't.

### bookmarks-data-probe.mjs

Users, lifecycle and retention on the real store: deleted users, a restored data folder (`webchecks/users-banner.json`),
export and import, snapshots, retention. Needs a Debug build (`DeveloperClockOffsetDays`); two throwaway users
(`tmp-orphan`, `tmp-delete`) get random passwords held in memory only.

```powershell
node scripts/bookmarks-data-probe.mjs --sandbox default [--timeout 300] [--skip-webcheck] [--out <file>]
```

It copies `bookmarks.json`, its `.bak`, `kit-users.json`, `snapshots/` and `archive/` first and puts them back while
stopped (`bookmarks.json` compared byte for byte); it holds a season and a movie folder out.

### bookmarks-je-web-probe.mjs

What the API walks of `je` can't show, in the web client: the switch-off on JE's and Custom Tabs' own pages, keys,
coexistence, Use B and Hidden Content.

```powershell
node scripts/bookmarks-je-web-probe.mjs --sandbox je [--out <file>]
```

It changes JE's, Custom Tabs' and Bookmarks' settings, viewer's preferences and JE hidden content; in a `finally` puts
them back, restarts (twice if Plugin Pages needs it) and runs `Initialize-Sandbox.ps1 -Name je`. Its webchecks:
`je-name-dialog.json` (with `probes/je-spy-probe.js`: typing in our dialog triggers nothing of JE's),
`je-coexist.json` (both plugins' entries and buttons once each), `je-switchoff-save.json`, `je-switchoff-osd.json` and
`je-switchoff-ct-remove.json` (the switch-off on JE's and Custom Tabs' pages), `je-switchoff-disabled.json` (JE 12.11)
and `je-switchoff-grid-key.json` (JE 12.12) (the key changed without writing a blank key), `je-use-b.json`,
`je-hidden-content.json` (a hidden movie's group goes).

### bookmarks-upgrade-probe.mjs: 1.0.0 to the current release and back

The upgrade from the real 1.0.0 in a sandbox that ran it first, and the way back ("The upgrade from 1.0", "Going back to
1.0"). The probe only reads (in `seed` it makes bookmarks and clips through 1.0.0's API); the rest is the procedure.
Options: `--phase seed|upgraded|downgraded` (required), `--v1 <file>` (required with `upgraded`: the 1.0
`bookmarks.json` copied while stopped), `--expect-copy <name>` (the `.v1-*` copy an earlier upgrade made, to be reused).
`seed` writes a manifest; `upgraded` and `downgraded` compare every list and clip with it.
`webchecks/upgrade-names.json`: the upgrade's names are the rows' titles.

```powershell
git worktree add --detach C:\src\bm-v100 bookmarks/v1.0.0                           # a short path; removed at the end
dotnet publish C:\src\bm-v100\Jellyfin.Plugin.Bookmarks\Jellyfin.Plugin.Bookmarks.csproj -c Release -o <scratch>\bookmarks-1.0.0
./scripts/dev-server.ps1 -Name upgrade -Port 8119 -Plugins None -Community FileTransformation,PluginPages -Background
./scripts/Initialize-Sandbox.ps1 -Name upgrade
./scripts/Stop-Sandbox.ps1 -Name upgrade
#   1.0.0's DLL and PDB into .sandbox\upgrade\data\plugins\Bookmarks-v1.0.0 with the meta.json 1.0.0's Deploy-Plugin.ps1
#   wrote (a folder name -Plugins None leaves alone)
./scripts/dev-server.ps1 -Name upgrade -Port 8119 -Plugins None -Community FileTransformation,PluginPages -Background
node scripts/bookmarks-upgrade-probe.mjs --sandbox upgrade --phase seed
./scripts/Stop-Sandbox.ps1 -Name upgrade                                              # then copy bookmarks.json aside as v1.json
./scripts/Install-RealServer.ps1 -Plugin Bookmarks -DataDir .sandbox\upgrade\data -BackupRoot <scratch> -WhatIf
./scripts/Install-RealServer.ps1 -Plugin Bookmarks -DataDir .sandbox\upgrade\data -BackupRoot <scratch>
#   the newest bookmarks/v* tag's build (add -FromWorkingTree to upgrade to the working tree instead); it stays in
#   artifacts\deploy\Bookmarks\Release, which the next line deploys again
./scripts/dev-server.ps1 -Name upgrade -Port 8119 -Community FileTransformation,PluginPages -Configuration Release -NoBuild -Background
node scripts/bookmarks-upgrade-probe.mjs --sandbox upgrade --phase upgraded --v1 v1.json
node scripts/webcheck.mjs --sandbox upgrade --steps upgrade-names.json
node scripts/bookmarks-api-probe.mjs --sandbox upgrade --fixture v1.json
#   "Going back to 1.0": stopped, plugins\Bookmarks_<version> deleted, the backup's
#   plugins\Bookmarks-v1.0.0 copied back, bookmarks.json replaced by the bookmarks.json.v1-* copy, then
#   dev-server.ps1 ... -Plugins None
node scripts/bookmarks-upgrade-probe.mjs --sandbox upgrade --phase downgraded
#   stopped, started again with the current release (Release, -NoBuild): the same upgrade, reusing the copy
node scripts/bookmarks-upgrade-probe.mjs --sandbox upgrade --phase upgraded --v1 v1.json --expect-copy bookmarks.json.v1-<yyyyMMddHHmmss>
./scripts/Stop-Sandbox.ps1 -Name upgrade
git worktree remove --force C:\src\bm-v100
```

### bookmarks-clips-probe.mjs

The My Clips endpoints through the API ("REST API", "The clips"): every clip state, filters, `Status`, `BulkDelete`, and
that admin never sees `viewer2`'s clip.

```powershell
node scripts/bookmarks-clips-probe.mjs --sandbox default [--timeout 300] [--out <file>]
```

`--timeout` seconds per clip (300). It sets `ClipOutputPath` for two clips to its `usb-clips` (inside the sandbox, so
`dev-server.ps1`'s clip path check passes; it stays in `ClipRoots`), unplugs it, and holds two clip files out. It deletes
one file itself: the held copy of a clip Bookmarks made again under its name.

### bookmarks-clip-sweep-probe.mjs

The daily maintenance's missing-clip sweep ("Missing clip files"): marking missing files, leaving out folders that
aren't there, the retention after `MissingClipRetentionDays`, clip folder markers (`.jfbm-clip-root`) and the admin's
Confirm, and the default clip folder deleted. Needs a Debug build (the clock moves by `DeveloperClockOffsetDays`).

```powershell
node scripts/bookmarks-clip-sweep-probe.mjs --sandbox default [--timeout 300] [--out <file>]
```

`--timeout` seconds per clip (300). It refuses to run while a clip not its own is or would be marked missing (the clock
would let retention take it). It changes `ClipOutputPath`, the retention and the clock, moves files and folders aside,
makes a stray `e1-drive\clips\admin`, and moves the default folder to `default-clips-away`; to put that back it moves
Bookmarks' re-made folder to `default-clips-remade` only while it holds just its marker and empty folders (**anything
else stops the probe: fix by hand**; a killed run's next start does the same, then runs the maintenance once). It
deletes only held files of clips Bookmarks let go and old markers, and leaves one archived record on purpose (only an
admin's *Purge* deletes from the archive). Each run's daily snapshot rotates older ones out. An exact copy of a clip
folder, taken after its marker was last written, can't be told from the folder.

### bookmarks-clips-page-probe.mjs

The My Clips page in the web client ("The My Clips page", "Row actions"), plus its shell and `index.html` fragment over
raw HTTP. Needs `Long Test Movie (2021)` and `Test Series (2020) S01E01`.

```powershell
node scripts/bookmarks-clips-page-probe.mjs --sandbox default [--skip-layouts] [--actions-only] [--timeout 300] [--out <file>]
node scripts/bookmarks-clips-page-probe.mjs --sandbox bare --deep-link-only      # and baseurl, one at a time
node scripts/bookmarks-clips-page-probe.mjs --sandbox je --je
```

`--skip-layouts` leaves out TV and phone; `--deep-link-only` seeds three clips and runs only the deep link;
`--actions-only` only the row actions (not with `--deep-link-only` or `--je`); `--je` runs `page-clips-je.json` and the
deep link; `--timeout` seconds per clip (300). It changes `ClipOutputPath` (`usb-clips`, `cp-usb`, `cp-stray`) and
`VideoPreset` (slow while long clips must still run), holds a file out, unplugs folders, makes an empty `cp-stray`, and
holds a file open during `page-clips-inuse.json`; it deletes only a forgotten clip's held copy.

| Webcheck | What it checks |
| --- | --- |
| `page-clips.json` | the list, live progress, filters and history |
| `page-clips-tv.json`, `page-clips-phone.json` | the TV and phone layouts |
| `page-clips-deeplink.json` | a cold deep link (every sandbox) |
| `page-clips-actions.json` | each state's row actions |
| `page-clips-inuse.json` | deleting a held clip file |
| `page-clips-je.json` | JE's tags leave our rows alone |
| `page-clips-pluginpages.json` | by hand: polling stops in Plugin Pages' hidden cached view |
| `page-clips-off-pluginpages.json` | by hand, after `pages-pluginpages.json`'s warm-up: the page turned off while Plugin Pages' link remains |
| `page-clips-focus.json` | by hand after both page probes: keyboard focus in the row dialogs |

`webchecks/probes/bookmarks-requests-probe.js` (via `initScript`) records only the requests whose stack runs through
`/Bookmarks/ClientScript`, so jellyfin-web's own can't fail the "no core items request" step; it makes `window.fetch` an
accessor so other plugins' wrappers can't come between (`scripts/tests/bookmarks-requests-probe.test.mjs`). The by-hand
runs need the Auto host mode (a restart); put the settings back after:

```powershell
./scripts/Set-SandboxPluginConfig.ps1 -Name default -Plugin Bookmarks -Set @{ PageHostMode = 'Auto'; VideoPreset = 'slow' }
./scripts/Stop-Sandbox.ps1 -Name default; ./scripts/dev-server.ps1 -Name default -Background -NoBuild
node scripts/webcheck.mjs --sandbox default --steps page-clips-pluginpages.json      # or page-clips-off-pluginpages.json
./scripts/Set-SandboxPluginConfig.ps1 -Name default -Plugin Bookmarks -Set @{ PageHostMode = 'FallbackOnly'; VideoPreset = 'veryfast'; EnableClipsPage = $true }
```

### bookmarks-clip-rename-probe.mjs

Renaming a clip and its file ("The My Clips page"): the API on real files, its refusals, the dialogs
(`webchecks/page-clips-rename.json`, `page-clips-rename-tv.json`) and a crash between a rename's move and its record's
write. Runs in `default`, `je`, `bare` and `baseurl`.

```powershell
node scripts/bookmarks-clip-rename-probe.mjs --sandbox default [--skip-restart] [--skip-webchecks] [--timeout 180] [--out <file>]
```

`--skip-restart` leaves out the crash simulation (use it in `bare` and `baseurl`); `--timeout` seconds per clip (180).
It changes `ClipOutputPath` (`usb`, `stray`) and `VideoPreset`, unplugs folders, makes an empty `stray` and holds a file
open. The crash simulation, while stopped, writes two journal entries (`clip-rename-<id>.json`, recorded in
`planted.json`) and moves one file (recorded `adopted`: dropped, not undone, once a record names the new path), then
starts. An entry identifies the file only by size and last write time (Node's `stat` has no file id). Bookmarks, never
the probe, deletes journal entries.

### bookmarks-clips-admin-probe.mjs

The admins' **All users** scope of My Clips ("The admins' All users view"): who may call `/Bookmarks/Admin/Clips`, that
the note never shows, and the page (`webchecks/page-clips-admin.json`, `page-clips-admin-tv.json`). Runs in `default`,
`je`, `bare` and `baseurl`.

```powershell
node scripts/bookmarks-clips-admin-probe.mjs --sandbox default [--skip-webchecks] [--timeout 180] [--out <file>]
```

`--timeout` seconds per clip (180). It changes `VideoPreset` and `AdminsCanManageAllClips`, holds a file out, and makes
an API key (`clip-admin-probe`) and a test user (`bm-admin-probe-user`, id in `test-user.json`; password and token in
memory only; demoted before disabling, as Jellyfin won't disable an admin), deleted at the end and at the next start.

### The JSON store checks

The kit's JSON store (`kit/README.md`, "JSON stores"): an unreadable `bookmarks.json` is restored from snapshots or left
read-only (`DataUnreadable`), never started empty on its own; an unreadable `.bak` is moved aside and the snapshots
tried, a load tried again after a 503 remembers what it moved, and the clock moved back keeps every snapshot. The Data tab is the way out of `DataMissing` and
`DataUnreadable`. `webchecks/store-recovery.json` (admin) checks Restore and Start empty there; `store-banner.json`
accepts `DataUnreadable` too. Set up while stopped: make `bookmarks.json` unreadable (or delete it and its `.bak`),
leave only unusable snapshots (a newer schema's, or a file not JSON from its first byte; one whose start reads as a
`Version` lists as that schema and its Restore is 409); for the Restore run copy a readable snapshot into `snapshots`
once up.

### More Bookmarks webchecks

```powershell
node scripts/webcheck.mjs --sandbox default --steps player-set-end.json            # also je, bare and baseurl
node scripts/webcheck.mjs --sandbox default --steps player-timeline.json           # also je, bare and baseurl
node scripts/webcheck.mjs --sandbox default --steps player-replay-from-page.json   # also je, bare and baseurl
node scripts/webcheck.mjs --sandbox default --steps page-clips-theme.json
node scripts/webcheck.mjs --sandbox je --steps je-tv-reviews.json
```

| Webcheck | What it checks |
| --- | --- |
| `player-set-end.json` | the player's Set end time |
| `player-timeline.json` | bookmarks on the position slider, also at phone width |
| `player-replay-from-page.json` | Set end time after plays from My Bookmarks, with jellyfin-web's hidden player views |
| `page-clips-theme.json` | My Clips in four themes (a reload right after a hash navigation lands on Home, so it reloads the settled page) |
| `je-tv-reviews.json` | `je` only: the kit's mitigation of JE 12.14's TV trap, where JE's closed Reviews section catches the D-pad |

The player checks ignore the stale `/CustomRatings/script` tag and "player cannot be null", so they pass in `baseurl`.
`je-tv-reviews.json` puts the user's JE `ReviewsExpandedByDefault` back as it found it (JE saves it at every open and
close); TMDB reviews aren't covered (no TMDB access). Workarounds: `plugins/Bookmarks/DEVELOPMENT.md`, "Notes and
limitations".
## User Ratings checks

User Ratings' API probes (`scripts/userratings-*-probe.mjs`) and webchecks (`scripts/webchecks/userratings-*.json`);
the design is in [plugins/UserRatings/DEVELOPMENT.md](../plugins/UserRatings/DEVELOPMENT.md).
- Every probe takes `--sandbox <name>`, `--out <file>` and `--help`, plus the options listed below.
- Every probe needs `Initialize-Sandbox.ps1`'s users `admin`, `viewer` and `viewer2` and the standard media.
- Every probe deletes the ratings it made, as their owner, and puts back the settings, policies and played states it
  changed, in a `finally`.
- `ScriptsTests.UserRatings<Name>Probe_KeepsItsSafetyGuards` pins those guards for every probe except the JE import
  probe.

### First checks

```powershell
./scripts/dev-server.ps1 -Name default -Background          # User Ratings creates data/userratings/userratings.db
./scripts/Initialize-Sandbox.ps1 -Name default
node scripts/index-probe.mjs --sandbox default --plugin UserRatings      # the data-jfur-plugin tag at the plugin's version
node scripts/webcheck.mjs --sandbox default --steps kit-registry.json    # window.__jfkit.jfur, both sides' versions
node scripts/webcheck.mjs --sandbox default --steps userratings-settings-page.json
./scripts/Stop-Sandbox.ps1 -Name default
```

`kit-registry.json` takes each plugin's route from its tag's `src`, because `<plugin name>/Admin/Diagnostics` fails for
a name with a space ("User Ratings").

### The probes

| Probe | Extra options | What it does |
| --- | --- | --- |
| `userratings-busy-probe.mjs` | `--hold <s>` (20 to 600 s, default 60): how long the holder keeps the lock | Checks that a database locked for more than 5 s answers 503 "busy" within 7 s, even for concurrent writes. Reads keep working (WAL), and a write succeeds at once after the lock is released. |
| `userratings-api-probe.mjs` | `--readonly-drill`; `--keep-files` (keeps the restart logs and the drill backup in `api-probe/`) | See "User Ratings API probe". |
| `userratings-visibility-probe.mjs` | `--remove-media` (deletes its ~6 MB of media copies, which are kept by default); `--details` (repeats the check in the web client as viewer2) | Checks that no list or answer reveals a copy of an item the member can't open. |
| `userratings-details-probe.mjs` | none | Seeds more than a page of reviews and runs `userratings-details-reviews.json` as viewer2. |
| `userratings-settings-probe.mjs` | none | Checks the Settings API: access, reads, dry runs, refusals with their codes (nothing saved), a full save and its revert, and the 409 `SettingsChanged`. |
| `userratings-data-probe.mjs` | none | Checks the Data API: export jobs, a Merge or Replace import guarded by its plan token, restore with its undo snapshot, and the 404 for Relink and Purge. Deleted words are checked to be gone from `userratings.db` and its `-wal`. |
| `userratings-lifecycle-probe.mjs` | `--timeout <s>` (wait per scan, default 300); `--keep-files` (keeps the hold folder) | See "User Ratings library lifecycle probe". |
| `userratings-je-import-probe.mjs` | `--webchecks` (also runs `userratings-data-je-desktop.json`) | See "User Ratings JE import probe". |

What each probe changes and how it cleans up:
- **Busy:**
  - It rates a Movies film the admin hasn't rated, through the member API, so no Debug build is needed. It deletes
    the rating at the end.
  - A separate `node:sqlite` process holds `BEGIN IMMEDIATE` on the sandbox's own `data/data/userratings/userratings.db`.
  - It refuses a database outside `.sandbox/<name>/` or under `C:\ProgramData`.
- **Visibility:**
  - Why it exists: on 10.11.5, `GetItemList` checks only the item's own `IsVisible(user)`, while `GetItemById` also
    checks the parents. Lists built from `GetItemList` once leaked hidden copies.
  - It makes media copies in `.sandbox/<name>/visibility-probe/media`, three libraries (`UR Visibility A`, `B`,
    `Movies`) with parental ratings, and two users (`urvis-kid`, `urvis-tagger`, random passwords held in memory).
  - The series is rated before its episodes are locked, because on 10.11.5 editing a series overwrites the episodes'
    locked rating.
  - Clean-up:
    - `visibility-probe/state.json` records what it made, so the next run can clean up after a run that stopped half
      way. The admin resets a leftover user's password to sign in again.
    - It refuses a library name that already exists and isn't in its state.
    - It writes nothing outside its folder.
- **Details:** it makes 23 synthetic members (`urdetails-01` …, random passwords held in memory) who review Long Test
  Movie, and deletes them at the end. `details-probe/state.json` lets the next run clean up a run that stopped half way.
- **Settings:** it uses device id `ur-settings-probe`. It posts the configuration back as it read it, in a `finally`,
  and checks that the result is byte for byte the same.
- **Data:**
  - Run it only on a sandbox whose ratings are synthetic: it replaces them with an export of themselves.
  - It reads only `sandbox.json`, `credentials.json`, `userratings.db` and its `-wal`, and refuses a data folder outside
    `.sandbox`.
  - It uses device id `ur-data-probe`.

### User Ratings API probe

`node scripts/userratings-api-probe.mjs --sandbox default [--readonly-drill] [--keep-files] [--out <file>]`

- **Needs:** the libraries Movies and Movies 2. It refuses to run unless it finds films that none of the three users has
  rated, so it never deletes someone else's rating.
- **Covers:** the member API as a curl session would walk it:
  - who may call: an API key gets 403;
  - the reads and writes, the errors (400, 413 from Kestrel, one 404);
  - the `IfUpdatedUtc`, `IfReviewUpdatedUtc` and `IfNoReview` preconditions;
  - concurrent PUTs, which leave one row;
  - the rules `WhoCanRate`, `EnableRatings` and `RequirePlayedToRate`;
  - lost library access (`Mine/Inaccessible`, `DELETE Mine/{ratingId}`).
- **What it changes and puts back:**
  - the configuration;
  - viewer2's policy;
  - a played state;
  - an API key, deleted at once and again in the `finally`.
- **`--readonly-drill`:**
  1. Stops the sandbox (`Stop-Sandbox.ps1`) and copies the database to `api-probe/drill-backup`.
  2. Sets `PRAGMA user_version = 99`.
  3. Restarts with `dev-server.ps1 -Port <its port> -Background -NoBuild` and checks that reads still work, every write
     gets 503, and `Can.Reason` is `ReadOnly`.
  4. Stops it, puts the copy back (the same SHA-256) and restarts.

  A drill that stops half way puts the copy back in the `finally`.
- **Clean-up:**
  - `api-probe/created.json` lists the ratings before they are made, so the next run can clean up after a run that
    stopped half way.
  - `Records.Attached` ends where it started. Each delete leaves a tombstone (ids and times only).

### User Ratings library lifecycle probe

`node scripts/userratings-lifecycle-probe.mjs --sandbox default [--keep-files] [--timeout <s>] [--out <file>]`

- **Needs:** the `default` sandbox with its Music library (`Initialize-Sandbox.ps1` adds it, with its media, when they
  are missing), and the database at the current schema (4).
- **How it works:**
  - It uses its own media, copied from the seed's clips, plus the seed's Music folder.
  - A scan is `POST /Library/Refresh`, followed by waiting for the `RefreshLibrary` task.
  - The verify sweep is the `UserRatingsMaintenance` task.
- **Covers:** a rated item's life, except archiving:
  - renamed or moved: the rating follows, with the same id;
  - two copies: the rating is Ambiguous, the banner shows with one Activity Log entry, and `Detached/Seen` ends it;
  - the `OtherCopies` hint, and its absence when the member can't open the other copy;
  - deleted and restored;
  - 10.11.5's cache ghost: the rating stays detached, and the read says `NotInLibrary`;
  - folders that disappear (unmounted, an empty mount point, a dangling junction, a `removeRoot` wipe, a mass detach):
    the ratings are held, never detached, until the folder is back. `POST Admin/Held/Detach` is admin only.
  - The log lines carry ids, never titles or paths.
- **What it changes:**
  - It renames, copies and deletes only inside `.sandbox/media` and `.sandbox/<name>/lifecycle-probe/hold`.
  - It makes one junction (and removes only the link) and one text file in `Music`.
  - It renames the Music library and back, in a `finally` and again at the next start.
  - It never deletes the seed's artist folders. If one couldn't go back, the hold folder stays, and the next run first
    puts the folders, the junction's target and the Music folder back.
  - At the end its ratings are deleted, its media is scanned away, and `Records` is as it was before.

The users' lifecycle (`DeletedUserPolicy`, orphans, `Users/ApplyPolicy`, `Users/Remap`, the merged-version step) has no
probe. After a change there, run this probe and the details and chip webchecks in `default`, then the `je` ones.

### User Ratings JE import probe

`node scripts/userratings-je-import-probe.mjs --sandbox je [--out <file>] [--webchecks]`

- **Needs:** the `je` sandbox (Jellyfin Enhanced 12.14), where admin, viewer and viewer2 have rated none of the
  `[tmdbid-]` films.
- **What it changes:**
  - It writes a synthetic `reviews.json` in JE's format, with edge cases such as a two-copy film, an unknown member, an
    episode the library lacks and a future date. The file goes into the sandbox's
    `plugins/configurations/Jellyfin.Plugin.JellyfinEnhanced/`.
  - It puts back the file it found there, and refuses a JE folder outside `.sandbox`.
  - It shows viewer2 on the sign-in screen meanwhile, then puts the policy back.
- **Covers:**
  - access, and the 404 and 400 for a missing file or an unknown format;
  - the dry run (no review text in it) and the import's rules (rounded, clamped, never a spoiler, `Copies = First` or
    `All`);
  - an episode key in JE 12.14's shape, stored as detached;
  - re-imports (`AlreadyImported`, `SkippedDeleted`, `Updated`) and the undo by snapshot restore;
  - the switch-over checklist;
  - JE's own files unchanged (checked by SHA-256).

### Webcheck probes and generators

| File (in `webchecks/`) | Purpose |
| --- | --- |
| `probes/userratings-stars-probe.js` | Mounts the shipped star widget, taken from the served ClientScript, on a details page (`__jfurStars.mount`) with a stubbed save, and reports its keys, styles and collisions. |
| `probes/userratings-details-probe.js` | Logs the details section's `/UserRatings/` requests, can answer one in the server's place (`inject`: `stub`, `after`, `partial`, `patch`), and reads the server and the section's state as the member. The token stays in the page. |
| `probes/je-spy-probe.js` | JE's key-action spies (see "Jellyfin Enhanced interop"). |
| `userratings-version.mjs` | Exports `VERSION` and `SHORT_VERSION` from `plugins/UserRatings/build.yaml`. Every generator pins this version. |

The generators write every webcheck below except `userratings-settings-page.json`:
- `gen-stars.mjs`: `userratings-stars-*`;
- `gen-details.mjs`: `userratings-details-*`, the chip ones included;
- `gen-moderation.mjs`: `userratings-moderation-*` and `userratings-remove-*`;
- `gen-settings.mjs`: `userratings-settings-desktop`, `-tv` and `-phone`;
- `gen-data.mjs`: `userratings-data-*` and `userratings-detached-desktop`.

To rerun one: `node scripts/webchecks/gen-<name>.mjs [--out <folder>]` (the default folder is `scripts/webchecks`).
- After a version bump in `build.yaml`, or an edit to a generator, run all five and commit their output.
- Never hand-edit their JSON: `ScriptsTests.<Stars|Details|Moderation|Settings|Data>Webchecks_AreWhatTheirGeneratorWrites`
  fails on any difference.

### User Ratings webchecks

Run each one with `node scripts/webcheck.mjs --sandbox <sandbox> --steps <file> --out <results>/<name>.json` (see
"webcheck.mjs").
- A webcheck that rates deletes the member's rows on its items at the start and again at the end.
- The settings webchecks stop unless the settings they change are at their defaults.

| Webcheck | Sandbox, user | Checks |
| --- | --- | --- |
| `userratings-stars-desktop.json` | default, viewer | The star widget's accessibility, keys, mouse, Clear, failed save, disabled state, forced colours, reduced motion and RTL. No API request is made. |
| `userratings-stars-tv.json`, `userratings-stars-touch.json`, `userratings-stars-pen.json` | default, viewer | The widget with the D-pad, touch (390x844) and a pen. |
| `userratings-stars-je.json` | je, admin | The slider's keys work next to JE's listener; no style collisions with JE. |
| `userratings-details-desktop.json`, `userratings-details-experimental.json` | default, viewer | The details section: saving, reviews, Clear, injected errors, pending saves across navigation, Delete, the review preconditions. Also in the experimental layout. |
| `userratings-details-tv.json`, `userratings-details-touch.json` | default, viewer | The section with the D-pad, and with touch (390x844). |
| `userratings-details-reviews.json` | default, viewer2 | The reviews list (average, paging, sort orders, isolation). Run only by the details probe: alone, it stops at its eighth step. |
| `userratings-details-windows.json`, `userratings-details-three-windows.json` | default, viewer in 2 or 3 windows | The review's 409 across windows; Reload and Overwrite keep every text. |
| `userratings-details-part2.json` | default, viewer, viewer2, admin | Spoilers, the 409 panel, drafts (cleared at sign-out), and the rating rules in the UI. |
| `userratings-details-je.json` | je, admin | The section beside JE's reviews; the TV D-pad past JE's closed Reviews. |
| `userratings-details-chip.json`, `userratings-details-chip-tv.json`, `userratings-details-chip-touch.json` | default, viewer | The member-average chip and the Rate button. `-chip.json` rates Test Album A, so it runs in `default` only. |
| `userratings-details-chip-je.json` | je, admin | Rate only where JE draws Reviews. Neither its exact place before More nor the chip being last is asserted, because other plugins may add items there. |
| `userratings-moderation-desktop.json`, `userratings-moderation-tv.json`, `userratings-moderation-phone.json` | default, admin | The Moderation tab: removals with a dry-run token, the 409 for a stale token, a synthetic RTL-named member (created and deleted by the run), TV focus, phone layout. |
| `userratings-remove-desktop.json`, `userratings-remove-experimental.json`, `userratings-remove-je.json` | default (`-je.json`: je), admin, viewer | "Remove…" on the details page, admin only. It finds the dialog by `.jfur-panel[role="dialog"]`, because JE puts a hidden dialog first in the page. |
| `userratings-settings-desktop.json`, `userratings-settings-tv.json`, `userratings-settings-phone.json` | default, admin (+viewer, viewer2) | The Settings tab end to end; every setting is put back. |
| `userratings-settings-page.json` | default, admin | Hand-written: the tabs' keyboard behaviour, one setting saved alone, Diagnostics' `userratings.db (schema 4, Ok)` (update this file when the schema changes). |
| `userratings-data-desktop.json` | default, admin, viewer | Export, import, restore and *Export for Jellyfin Enhanced*. Downloads are kept in the page, so no file is written. Nothing re-reads the export inside JE. |
| `userratings-detached-desktop.json` | default, admin, viewer | The detached list, Relink and Purge, with detached ratings made by a synthetic import. |
| `userratings-data-tv.json`, `userratings-data-phone.json` | default, admin | The Data tab with the D-pad and on a phone; nothing is restored. |
| `userratings-data-je-desktop.json` | je, admin | Run by the JE import probe's `--webchecks`: the JE import UI. The import is undone through the API. |

`je-tv-reviews.json` belongs to the Bookmarks checks. Run it with the `je` webchecks, because both plugins' kits register
`kit.je.tvSectionJump` there. User Ratings' registration is pinned by `details.test.mjs`.

### The full User Ratings pass

Run one sandbox at a time, on one port. The data webchecks and the JE import probe are run with
`node --max-old-space-size=2048`.

```powershell
./scripts/dev-server.ps1 -Name default -Port 8108 -Background
# every userratings-*.json but the je ones and -details-reviews; then the api (--readonly-drill), visibility (also
# --details), settings, details, data, busy and lifecycle probes
./scripts/Stop-Sandbox.ps1 -Name default
./scripts/dev-server.ps1 -Name je -Port 8108 -Background
./scripts/Initialize-Sandbox.ps1 -Name je
node --max-old-space-size=2048 scripts/userratings-je-import-probe.mjs --sandbox je --out <results>/je-probe.json --webchecks
# userratings-details-je, -details-chip-je, -remove-je, -stars-je; je-tv-reviews.json
./scripts/Stop-Sandbox.ps1 -Name je
./scripts/dev-server.ps1 -Name bare -Port 8108 -Background      # our plugins only; then baseurl (BaseUrl /jf), the same
./scripts/Initialize-Sandbox.ps1 -Name bare
# userratings-details-desktop, -details-tv, -stars-desktop, -settings-desktop, -settings-page, -data-desktop,
# -moderation-desktop, -remove-desktop; the API, visibility, details, settings and data probes
./scripts/Stop-Sandbox.ps1 -Name bare
```

Also run:
- `index-probe.mjs --plugin UserRatings`;
- `api-docs-probe.mjs --summary`;
- `Test-WebAnchors.ps1 -Sandbox default -Plugin UserRatings`.

The probes' version check accepts any User Ratings from 1.0 on.
## Library Maintenance checks

Library Maintenance is admin-only. Its checks cover the Dashboard page, the API and Move's item-menu entry. Each check
restores what it changes:
- Only the settings webcheck changes settings. Its last steps check that the settings' version is the starting one.
- Only the freshness probe touches media: one movie folder of its own.
- The access probe and index `--setups` add their own libraries. The access probe also makes `viewer2` an admin.
- The themes webcheck sets `admin`'s dashboard theme and restores it before asserting.
- A rebuild only replaces the in-memory index.

| Webcheck (`default` unless noted) | What it checks |
| --- | --- |
| `librarymaintenance-admin-script.json` | first open, `&tab=` links, reloads; `viewer` gets 403 |
| `librarymaintenance-index.json` | the index's movies, status line, Rebuild, phone |
| `librarymaintenance-tv.json` | TV-layout D-pad focus |
| `librarymaintenance-groups.json` (+`je`, `baseurl`) | duplicate cards, modes, filter, Open, phones, TV |
| `librarymaintenance-themes.json` (+`je`, `baseurl`) | 4.5:1 contrast in four themes, via the read-only `probes/librarymaintenance-contrast-probe.js`. Needs a Conflict group and a movie with no poster |
| `librarymaintenance-settings.json` (+`je`, `baseurl`, `scale`) | Export/Import, the preview dialog, the switches |
| `librarymaintenance-scroll.json` (`scale`) | 250+ groups: Load more, frame times, DOM growth |

```powershell
node scripts/webcheck.mjs --sandbox default --steps librarymaintenance-admin-script.json --fail-on-exception
```

In `baseurl`, run without `--fail-on-exception`. The stale `/CustomRatings/script` tag throws there, and the last step
checks that nothing else did.

The settings webcheck needs finding duplicates on, no library chosen, no folder excluded, and
`Movies 2/Amelie (2001)`. A run that stops early leaves the settings changed, and the next run refuses to start until
you restore them.

### Library Maintenance probes

Every probe takes `--sandbox <name>` (required) and `--out <file>`. They use loopback only, never port 8096, and never
print the tokens from `credentials.json`. Apart from `--out` they write nothing, except the freshness probe's record.
The `ScriptsTests.LibraryMaintenance*Probe_KeepsItsSafetyGuards` tests pin this.

| Script | Options (default) | What it checks |
| --- | --- | --- |
| `librarymaintenance-index-probe.mjs` | `--setups`, `--rebuilds <1-50>` (1), `--timeout <build s>` (300) | access, rebuilds, counts against `/Items`. `--rebuilds n` samples the working set of `jellyfin.exe`, whose PID it checks against `sandbox.json` |
| `librarymaintenance-freshness-probe.mjs` | `--phases add,scan` (both), `--library <name>` (Movies), `--cold`, `--timeout <scan s>` (900) | the index stays current after changes and scans. `--cold` replaces the phases and needs a freshly started server |
| `librarymaintenance-groups-probe.mjs` | `--rounds <1-10>` (3) | the groups API, plus timings (`evidence.requestMs`) |
| `librarymaintenance-access-probe.mjs` | `--timeout <scan s>` (600) | an admin limited to Movies (`viewer2`) sees only Movies |

Clean-up and recovery:
- **index `--setups`**: adds "LM Probe …" libraries (over `.sandbox/media` only) and merges an edition pair. A
  `finally` splits the pair and removes the libraries with `refreshLibrary=true`. Leftovers are removed at the next
  start.
- **freshness `add`**: records "Freshness Probe Movie (2026)" in `.sandbox/<name>/freshness-probe/created.json`, then
  removes it at the end, on failure, or at the next start. `.sandbox/media` is shared, so another sandbox's scan can
  see the movie.
- **access**: adds a library, "LM Access Probe Shared". A `finally` revokes its key, restores `viewer2`, removes the
  library and rebuilds. It refuses to start if `viewer2` is already an admin; `Initialize-Sandbox.ps1` fixes that.

At scale (`scale`: 10,000 synthetic movies, port 8105):

```powershell
./scripts/New-SandboxMedia.ps1 -SyntheticMovies 10000 -SyntheticEpisodes 20
./scripts/dev-server.ps1 -Name scale -Port 8105 -Plugins LibraryMaintenance -Community None -Background -NoBuild   # Release: -Configuration Release, no -NoBuild
./scripts/Initialize-Sandbox.ps1 -Name scale -ScanTimeoutSec 3600
node scripts/librarymaintenance-index-probe.mjs --sandbox scale --rebuilds 5 --timeout 600 --out .sandbox/scale/index-probe/debug.json
```

### The move sandbox

Move's checks run on `move` (port 8101); the menu checks also run on `default` and `je`.

```powershell
$env:JFLM_MOVE_TEST_VOLUMES = '<repository>\.sandbox\move-media\Movies 2=d'   # Debug builds only
./scripts/dev-server.ps1 -Name move -Port 8101 -Background
```

`JFLM_MOVE_TEST_VOLUMES` takes `path=name` pairs separated by `;`. Each path goes on a made-up volume
`\\?\Volume{jflm-test-<name>}\`, so "another drive" can be tested on C:. Release builds ignore it.
`dev-server.ps1` removes only the inherited `JELLYFIN_*` variables from the server's environment, so this variable
reaches the server as it is.

The libraries are over `.sandbox/move-media`, built by a media seed that isn't included (any synthetic movie and
series folders will do). Run only Library Maintenance, File Transformation and Plugin Pages.

The Move webchecks run with `--fail-on-exception`. Their shared `initScript`,
`webchecks/probes/librarymaintenance-move-helpers.js`, logs the requests (never headers or tokens).

| Webcheck | Sandbox | What it checks |
| --- | --- | --- |
| `librarymaintenance-move-menu.json` | `default`, `je` | where the "⋮" entry appears and where it doesn't |
| `librarymaintenance-move-viewer.json` | `default`, `je`, `move` | `viewer`: no entry, no requests |
| `librarymaintenance-move-layouts.json` | any | TV layout (no entry), touch phone |
| `librarymaintenance-move-je.json` | `je` | alongside Jellyfin Enhanced's entry |
| `librarymaintenance-move-dialog.json` | `move` (Debug) | the preview's cases and `move.*` blockers; nothing moves |
| `librarymaintenance-move-dialog-release.json` | `move` (Release) | the same, without the other-drive cases |
| `librarymaintenance-move-phone.json` | `move` | the dialog on a touch phone |
| `librarymaintenance-move-keys.json` | `move` | keyboard-only focus |
| `librarymaintenance-move-blocked.json` | `move` | links and a file in use (needs `Set-MoveBlockedFixture.ps1`) |

```powershell
node scripts/webcheck.mjs --sandbox move --steps scripts/webchecks/librarymaintenance-move-dialog.json --out result.json --fail-on-exception
```

### gen-move-dialog-release.mjs

`node scripts/webchecks/gen-move-dialog-release.mjs [--out <folder>]` writes
`librarymaintenance-move-dialog-release.json` from the Debug webcheck (default folder `scripts/webchecks`). It stops if
the steps it edits have moved. Run it again after editing the Debug webcheck:
`ScriptsTests.MoveDialogReleaseWebcheck_IsWhatItsGeneratorWrites` compares its output with the committed file.

### Set-MoveBlockedFixture.ps1

This script sets up the fixture for `librarymaintenance-move-blocked.json` in the move media's `Movies` folder:
- two junctions, `Link Folder` and `Plain Loose (2005).trickplay`, pointing to empty targets in `.sandbox\move-blocked`;
- a hidden child process holding `Tagged Loose (2003) [tmdbid-910003].mkv` open for reading.

```powershell
./scripts/Set-MoveBlockedFixture.ps1 -Action Setup    # then run the webcheck at once
./scripts/Set-MoveBlockedFixture.ps1 -Action Remove
```

Options: `-Action Setup|Remove|Status` (required), `-HoldSeconds <5-600>` (120), `-MediaRoot <folder under .sandbox>`
(default `.sandbox\move-media`). Windows only.

- **Setup** records `Movies`' last-write time in `.sandbox\move-blocked\state.json`. It refuses if anything already
  exists. The holder exits by itself.
- **Remove** stops the holder (by its recorded PID and start time) and removes each junction with `rmdir` without
  `/s`. It then removes each target if it is empty. A path it didn't make, or a target that isn't empty, is left in
  place and named. It restores `Movies`' last-write time, using `CreateFileW` and `SetFileTime` to get past Jellyfin's
  watcher. Otherwise the watcher refreshes the library 60 s later.
- **Status** shows the links, the holder and the time.
- **Safety**: every path must be inside `.sandbox` on a local drive, also once junctions are followed. Share and device
  paths are refused, and so is `C:\ProgramData\Jellyfin`.
## Access Roles checks

The `accessroles-*-probe.mjs` scripts check Access Roles (`../plugins/AccessRoles/DEVELOPMENT.md`) in a running sandbox.

**What all the probes share:**
- **Options:** `--sandbox <name>` (required), `--out <file>` and `--help`.
- **Writes:** only their own users (one name prefix per probe), their own roles (`<Name> probe …`), the assignments
  they make and the settings named in their section.
- **Cleanup:** an earlier run's leftovers are removed at the start; the probe's own objects and settings are removed or
  put back in a `finally`.
- **End checks:** no other user's policy was written, and the log has no unexpected Access Roles WRN/ERR/FTL line.
- **Guard tests:** Repo.Tests `AccessRoles<Name>Probe_KeepsItsSafetyGuards` pins each probe's guards.
- **Needs:**
  - `credentials.json`'s `admin`, plus `viewer`/`viewer2` where a section says so (`Initialize-Sandbox.ps1`);
  - Jellyfin 10.11.5;
  - Access Roles Active at `build.yaml`'s version.
- **Database:** read through `node:sqlite`, read-only, never the password column.
- **Webcheck retries:** a webcheck runs again (3 to 6 times) when only its sign-in failed, because jellyfin-web
  sometimes shows the user cards instead of the login form.

**Run them on one start, in this order.** The starting states and the log checks depend on it. Any free port works;
run one sandbox at a time.

```powershell
./scripts/dev-server.ps1 -Name access -Port 8103 -Plugins AccessRoles -Community FileTransformation,PluginPages -Background
node scripts/accessroles-probe.mjs --sandbox access --webcheck
node scripts/accessroles-settings-locks-probe.mjs --sandbox access --webcheck
node scripts/accessroles-web-guard-probe.mjs --sandbox access
node scripts/accessroles-admin-page-probe.mjs --sandbox access --webcheck
node scripts/accessroles-roles-probe.mjs --sandbox access
node scripts/accessroles-sync-hooks-probe.mjs --sandbox access
node scripts/accessroles-sync-probe.mjs --sandbox access
node scripts/accessroles-sync-review-probe.mjs --sandbox access --race 48
node scripts/accessroles-admin-tabs-probe.mjs --sandbox access --webcheck
node scripts/accessroles-recovery-probe.mjs --sandbox access
node scripts/accessroles-data-probe.mjs --sandbox access
node scripts/accessroles-sync-hooks-race-probe.mjs --sandbox access
./scripts/Stop-Sandbox.ps1 -Name access
```

In the sections below, "the `access` sandbox" means the `dev-server.ps1` line above. The recovery probe's `--scratch`
mode, the JE probe and the suite probe each need their own start, given in their sections.

### Access Roles probe

Checks that the plugin loads: the plugin is Active, and its page, data file, Diagnostics, action manifest, client
script, maintenance task and log are as expected.

- **Run:** on the `access` sandbox, `node scripts/accessroles-probe.mjs --sandbox access [--webcheck]`.
- **Needs:** `admin` and `viewer`.
- **Writes:** nothing. Its one POST is a `ClientHealth` report.
- **`--webcheck`:** runs `webchecks/accessroles-settings.json` as admin. It expects `Roles` 0, so run this probe first
  on a start.

### Access Roles roles probe

Walks the roles and assignments API the way an admin's `curl` would (DEVELOPMENT.md, "The roles API"), including its
refusals, the pending release and the audit.

- **Run:** on the `access` sandbox, after the Access Roles probe: `node scripts/accessroles-roles-probe.mjs --sandbox access`.
- **Needs:** `admin`, `viewer`, `viewer2` and three libraries (the first `homevideos`, `movies` and `tvshows` ones).
- **Writes:**
  - only through `/AccessRoles/Admin/...`; every other request is a GET;
  - its own users `rolesprobe-guest` and `rolesprobe-solo`, which it creates and later deletes.
- **Library sync:** paused for the run and put back afterwards (`evidence.librarySyncRestored`).
- **End state:** the store is empty again.

### Access Roles sync probe

Checks the library sync (DEVELOPMENT.md, "The library sync"). It compares every policy field before and after, both
through the API and in the database, across writes, drift, pauses, the library lifecycle, a sign-in race and releases.

- **Run:** on the `access` sandbox, after the Access Roles and roles probes:
  `node scripts/accessroles-sync-probe.mjs --sandbox access [--race 8]`.
- **`--race <n>`:** the number of sign-in race attempts, 0 to 40 (default 8).
- **Needs:** `admin` and three libraries.
- **Writes:**
  - users `syncprobe-…`;
  - the library `Sync probe Lib` over `.sandbox/<name>/syncprobe/library`;
  - `EnforcementEnabled`, `LibrarySyncEnabled` and `DriftPolicy`, put back afterwards;
  - `SAFE-MODE`, removed afterwards.
- **Timing:** it waits 5.5 s after a write, 11 s after a library call and 31 s before its store change, so an earlier
  probe's sign-in runs have passed.
- **Expected warnings:** the race's.

### Access Roles sync review probe

Re-runs the repros of a review of the library sync: an admin's save racing `POST Sync`, renamed libraries, `ReportOnly`
with a re-created library, and pausing.

- **Run:** on the `access` sandbox, after the sync probe:
  `node scripts/accessroles-sync-review-probe.mjs --sandbox access [--race 26]`.
- **`--race <n>`:** the number of admin-save race attempts, 0 to 64 (default 26).
- **Needs:** `admin` and three libraries.
- **Writes:**
  - users `syncrace-…`;
  - two libraries over `.sandbox/<name>/syncrace/`;
  - one `POST /Library/Refresh`;
  - the three sync settings, put back afterwards.
- **Expected warnings:** the race's, in both directions.

### Access Roles sync hooks probe

Checks the user policy hooks, the default roles and the import (DEVELOPMENT.md, "The user policy hooks, default roles
and the import"). The import is tested through the status page's wizard, `webchecks/accessroles-import.json`.

- **Run:** on the `access` sandbox, after the roles probe and before the sync probes:
  `node scripts/accessroles-sync-hooks-probe.mjs --sandbox access`.
- **Needs:** `admin`, three libraries with items, and a store that manages nobody. Otherwise the import fails with 409
  `AlreadyManaging`.
- **Writes:**
  - users `hook-…`;
  - the default roles and the sync settings, put back afterwards;
  - the import's assignments of the sandbox's own non-admin users. These are store records only, removed with `Keep`.

### Access Roles sync hooks race probe

Checks the places where the hooks race the core:
- a user created while the database is busy;
- a sign-in right after a user is created;
- restoring a user created under default roles;
- the restore hold.

**Run:** on the `access` sandbox, last:
`node scripts/accessroles-sync-hooks-race-probe.mjs --sandbox access [--attempts 4] [--signins 6]`.

- **Ordering:** run it after the recovery and data probes, because its log check expects their warnings. Its own
  refused sign-in leaves a warning that the other probes' log checks don't expect, so nothing else should run after it.
- **`--attempts <n>`:** the number of locked-database creations, 1 to 20 (default 4).
- **`--signins <n>`:** the number of create-then-sign-in rounds, 1 to 30 (default 6).
- **Needs:** `admin` and three libraries with items.
- **Writes:**
  - users `hookrace-…`;
  - the default roles and the sync settings, put back afterwards;
  - `data/data/accessroles/library-sync-hold.json`, deleted afterwards.
- **The locker:** a child process that opens the sandbox's `jellyfin.db` (the path is asserted to be inside
  `.sandbox`, never `C:\ProgramData\Jellyfin`).
  - It waits for the new user's `UserCreated` activity entry, then holds `BEGIN IMMEDIATE` for 4 s and rolls back.
  - It waits for that entry because an earlier lock could block Jellyfin's activity logger, which runs before Access
    Roles' consumer, and that made the check flaky.

### Access Roles settings locks probe

Checks the settings enforcement (DEVELOPMENT.md, "The settings locks"). It tests refusals and merges for each section,
`/Me`, the counters, the hardening's settings rows and the manifest's classification, as a locked user, an unlocked
user, a locked administrator and an API key.

- **Run:** on the `access` sandbox, right after the Access Roles probe:
  `node scripts/accessroles-settings-locks-probe.mjs --sandbox access [--webcheck] [--refusals 20] [--no-audit-wait]`.
- **`--webcheck`:** also runs `webchecks/accessroles-settings-locks.json` as `viewer`. Viewer gets the probe's role with
  every section locked, and is unassigned afterwards with `Keep`.
  - Typed settings URLs end on Home, so the webcheck saves through `ApiClient`.
  - It also submits the pages' own forms with the guard switched off.
- **`--refusals <n>`:** the size of the password-attempt flood, 1 to 200 (default 20).
- **`--no-audit-wait`:** skips the final `RequestsBlocked` audit check. That check waits for a 10-minute window, which
  makes a full run take about 13 minutes.
- **Writes:**
  - users `lockprobe-…`, each with their own settings written back at once;
  - a lock-only role;
  - the API key `lockprobe-key`, revoked afterwards.
- **Never sends:** a server restart or a remote metadata search.
- **With Jellyfin Enhanced:** if the start includes `-Community …,JellyfinEnhanced`, it also posts invalid bodies to
  JE's saves. Without JE, that part is skipped.

### Access Roles web guard probe

Checks the web guard (DEVELOPMENT.md, "The web guard") in headless Chrome: first `index.html`'s head fragment, then the
webchecks below.

- **Run:** on the `access` sandbox, after the settings locks probe:
  `node scripts/accessroles-web-guard-probe.mjs --sandbox access [--only legacy,tv]`.
- **`--only`:** run only some webchecks. Values: `legacy`, `firstload`, `experimental`, `tv`, `legacy-guest`,
  `experimental-guest`, `mobile`, `admin`, `nolibraries`.
- **Needs:** `viewer`, `viewer2`, and nobody managed. It refuses to start otherwise.
- **Writes** (through the admin API only; everything is put back):
  - the roles `Guard probe Kid` and `Guard probe Guest`;
  - viewer's and viewer2's assignments (viewer2's undone with `Unmanage` `Restore`);
  - the default roles;
  - viewer's Display (account) keys in `usersettings`.
- **End check:** both users' configuration must be exactly as it was read, with null and `""` counted as equal.
- **Retries:** a scenario runs again only when its first sign-in failed
  (`AccessRolesWebGuardProbe_RunsAWebcheckAgain_OnlyWhenItsFirstSignInFailed`).

| Webcheck | User | Checks |
| --- | --- | --- |
| `accessroles-web-guard-legacy.json` | viewer, Kid | Legacy layout: locked pages hidden and unreachable, form saves kept |
| `accessroles-web-guard-firstload.json` | viewer, viewer2 | First load with slow `/Me` or script; switching users |
| `accessroles-web-guard-experimental.json` | viewer, Kid | Experimental layout: user menu, focus and form saves |
| `accessroles-web-guard-tv.json` | viewer, Kid | TV layout: the D-pad skips hidden links |
| `accessroles-web-guard-legacy-guest.json` | viewer, Guest | Every Settings link hidden, administrator note shown |
| `accessroles-web-guard-experimental-guest.json` | viewer, Guest | Only Sign out left in the user menu |
| `accessroles-web-guard-mobile.json` | viewer, Guest | Mobile layout: drawer, note and typed URLs |
| `accessroles-web-guard-admin.json` | admin | Not locked; banner on Library access; *Add user* names the default role |
| `accessroles-web-guard-nolibraries.json` | viewer2 | The `NoLibraries` message on Home |

### Access Roles admin page probe

Checks the admin page's Roles and Users tabs (DEVELOPMENT.md, "The admin page: Roles and Users").
- **API:** the page and `AdminScript`, the bulk-assign and role-delete races (each with a negative control), and
  Effective.
- **`--webcheck`:** the flows on desktop, TV and phone.

- **Run:** on the `access` sandbox:
  `node scripts/accessroles-admin-page-probe.mjs --sandbox access [--webcheck]`.
- **Needs:** no default role.
- **Writes:**
  - users `pageprobe-kid` and `pageprobe-teen`, with their assignments and synced access (written back with
    *Restore*);
  - roles `Page probe …`.

  Everything is removed afterwards. The webchecks only press the page's own buttons. The one exception is "another
  administrator"'s in-page `PUT`s, which set up the races.

| Webcheck (`--webcheck`) | Checks |
| --- | --- |
| `accessroles-admin-page-tv-confirm.json` | The import's confirm dialog and a stale *Apply* (skipped unless the import is offered) |
| `accessroles-admin-page-desktop.json` | Roles, bulk assign, user panel and role delete, with stale-save recovery |
| `accessroles-admin-page-tv.json` | TV focus and keys; read-only |
| `accessroles-admin-page-phone.json` | Phone layout fits the screen; read-only |

### Access Roles admin tabs probe

Checks the admin page's Overview, Libraries, Audit, Settings, Data and Diagnostics tabs and the per-user Revert
(DEVELOPMENT.md, "The admin page: Overview, Libraries, Audit, Settings, Data and Diagnostics").
- **API:** the Settings API, the library lifecycle, `Sync?userId=` with its races, and a settings save waking the sync.
- **`--webcheck`:** the tabs on phone, TV and desktop.

- **Run:** on the `access` sandbox:
  `node scripts/accessroles-admin-tabs-probe.mjs --sandbox access [--webcheck]`.
- **Needs:** no default role.
- **Writes:**
  - users `tabsprobe-…` and roles `Tabs probe …`;
  - three libraries over `.sandbox/<name>/tabsprobe`;
  - `DriftPolicy` and `ReconcileIntervalMinutes`, put back afterwards, with the libraries' references purged.

| Webcheck (`--webcheck`) | Checks |
| --- | --- |
| `accessroles-admin-tabs-phone.json` | Phone layout fits the screen; read-only |
| `accessroles-admin-tabs-tv.json` | TV focus and the *Review* dialog; nothing saved |
| `accessroles-admin-tabs-desktop.json` | Every tab's actions, including a stale settings save (Audit *Download* is captured in the page, never written to disk) |

### Access Roles recovery probe

Checks lockout and recovery (DEVELOPMENT.md, "Lockout and recovery").

**On the `access` sandbox,** it checks the native preference mirror and *Release all locks*. Run it before the sync
hooks race probe, which expects its warnings:

```
node scripts/accessroles-recovery-probe.mjs --sandbox access
```

**With `--scratch`,** it checks a failed store write, SAFE-MODE and enforcement across restarts, a broken, newer or
restored store at startup (Degraded mode), a narrow administrator, and the uninstall. These steps restart the server,
write the store's files and uninstall the plugin, so `--scratch` refuses any sandbox except `access-scratch`. That
sandbox must listen on loopback only:

```powershell
./scripts/dev-server.ps1 -Name access-scratch -Port 8104 -Plugins AccessRoles -Community None -Fresh -Background
./scripts/Initialize-Sandbox.ps1 -Name access-scratch -SkipScan -SkipApiDocs
node scripts/accessroles-recovery-probe.mjs --sandbox access-scratch --scratch
./scripts/Stop-Sandbox.ps1 -Name access-scratch   # then delete .sandbox/access-scratch
```

- **Needs:** two libraries.
- **Writes:**
  - users `recoveryprobe-…`;
  - assignments, removed with `Keep`;
  - settings, put back afterwards.
- **Passwords:** every password change it sends carries a wrong current password, so the change is always refused.
- **`--scratch` also changes:**
  - `SAFE-MODE`, removed afterwards;
  - `accessroles.json.tmp`, held open exclusively;
  - the data file and its `.bak`, copied to `.sandbox/access-scratch/recoveryprobe` and put back.
- **Afterwards:** the uninstall leaves a restart pending, so delete the scratch sandbox.

**Folder-delete recovery** (by hand, on the scratch sandbox):
1. Turn the mirror on and create an API key.
2. Stop the server and delete `data/plugins/Access Roles_<version>`.
3. Start with `-Plugins None -NoBuild`.
4. Run the plugin README's PowerShell line in Windows PowerShell 5.1. The flags come back on and no other policy field
   changes.
5. Delete the key.

`AccessRolesRecoveryLineTests` runs the same line against a loopback stand-in.

### Access Roles data probe

Checks export, Merge/Replace import with its guards, mapping users and libraries by name, snapshot restore with its
hold, and the Diagnostics actions (DEVELOPMENT.md, "Export, import and restore").

- **Run:** on the `access` sandbox, before the sync hooks race probe, which expects its warnings:
  `node scripts/accessroles-data-probe.mjs --sandbox access`.
- **Needs:** four libraries with folders, a store that is not Degraded, and a sync that is not held.
- **Writes:**
  - users `exportprobe-…`;
  - assignments, removed with `Keep`;
  - the store, through the probe's own imports and restore: it ends as the probe exported it.
- **End check:** the other users' policies and the settings must be unchanged.

### Access Roles JE probe

Checks Access Roles next to Jellyfin Enhanced 12.14, and Bookmarks if it is installed: JE's saves are refused under a
lock, bookmarks still work, and `/Items` respects both JE's Hidden Content and the role's libraries. It needs its own
start:

```powershell
./scripts/dev-server.ps1 -Name access -Port 8103 -Plugins AccessRoles,Bookmarks -Community FileTransformation,PluginPages,JellyfinEnhanced -Background
node scripts/accessroles-je-probe.mjs --sandbox access [--webcheck]
```

- **Needs:** an unmanaged `viewer`, one library with two movies, and another library with one movie.
- **Writes:**
  - users `jeprobe-…`, with their JE files and bookmarks;
  - viewer's assignment, removed with `Keep`;
  - JE's Hidden Content setting and one hidden movie, both put back afterwards.
- **`--webcheck`:** runs `webchecks/accessroles-je-legacy.json` and `accessroles-je-experimental.json` as `viewer`. They
  check that JE's entries are hidden and that its shortcuts end on Home.

### Access Roles suite probe

Checks isolation across the suite, with the released builds of Bookmarks and Library Maintenance next to Access Roles.
User Ratings is not included. Tag Cloud is checked only if it is installed; otherwise the result lists it under
`skipped`.

What it checks:
- a narrow user and a narrow administrator learn nothing about hidden items;
- anonymous callers reach only the scripts;
- the other plugins answer the same with Access Roles disabled;
- a demoted administrator loses every admin route at once (`webchecks/accessroles-suite-demote.json`).

It reports `findings` (`BM-admin-clips`, `JF-disable-deletes`) for a decision instead of failing on them.

To run it, deploy each release into the stopped `access` sandbox from a detached worktree with a short path, then
start the sandbox:

```powershell
git worktree add --detach <a short path> <the plugin's release tag>
./scripts/Deploy-Plugin.ps1 -Plugin LibraryMaintenance -DataDir .sandbox\access\data -JellyfinDir <a copy of the program folder> -SourceRoot <that short path> -Configuration Release
git worktree remove <that short path>
./scripts/dev-server.ps1 -Name access -Port 8103 -Plugins AccessRoles,Bookmarks -Community FileTransformation,PluginPages -Configuration Release -Background
node scripts/accessroles-suite-probe.mjs --sandbox access [--visible-library <name>]
```

- **`--visible-library <name>`:** the movie library the narrow role keeps. Default `Visible Movies`; it can't be
  `Movies` or `Movies 2`.
- **Needs:** `Movies`, and `Movies 2` with *Amelie (2001)* in it.
- **Writes:**
  - users `suiteprobe-…`, and admin2's admin flag;
  - the kid's bookmarks and one clip;
  - Access Roles' enabled state, enabled again at the end;
  - two in-process restarts.
- **Admin sweeps:** a write is sent only after every read was refused, and only with a body that can't bind.
## Smart Search checks

Smart Search (`plugins/SmartSearch/DEVELOPMENT.md`) in a running sandbox seeded by
`Initialize-Sandbox.ps1`, as `viewer`. Two webchecks for the page host, one per mode (the mode is read at startup, so
switching needs a restart), four for the page's search, four for its saved queries and four for its advanced form, which need no
community plugin (a sandbox with `-Community None` will do):

```powershell
./scripts/dev-server.ps1 -Name default -Port 8112 -Plugins Bookmarks,SmartSearch -Background
./scripts/Set-SandboxPluginConfig.ps1 -Name default -Plugin SmartSearch -Set @{ PageHostMode = 'Auto' }
./scripts/Stop-Sandbox.ps1 -Name default
./scripts/dev-server.ps1 -Name default -Port 8112 -Plugins Bookmarks,SmartSearch -Background -NoBuild
node scripts/webcheck.mjs --sandbox default --steps smartsearch-pluginpages.json   # PageHostMode=Auto
./scripts/Set-SandboxPluginConfig.ps1 -Name default -Plugin SmartSearch -Set @{ PageHostMode = 'FallbackOnly' }
./scripts/Stop-Sandbox.ps1 -Name default
./scripts/dev-server.ps1 -Name default -Port 8112 -Plugins Bookmarks,SmartSearch -Background -NoBuild
node scripts/webcheck.mjs --sandbox default --steps smartsearch-page.json          # PageHostMode=FallbackOnly
node scripts/webcheck.mjs --sandbox default --steps kit-registry.json
node scripts/webcheck.mjs --sandbox default --steps smartsearch-search-desktop.json
node scripts/webcheck.mjs --sandbox default --steps smartsearch-search-tv.json
node scripts/webcheck.mjs --sandbox default --steps smartsearch-search-phone.json
node scripts/webcheck.mjs --sandbox default --steps smartsearch-search-themes.json
node scripts/webcheck.mjs --sandbox default --steps smartsearch-saved-desktop.json   # first: the others use its queries
node scripts/webcheck.mjs --sandbox default --steps smartsearch-saved-tv.json
node scripts/webcheck.mjs --sandbox default --steps smartsearch-saved-phone.json
node scripts/webcheck.mjs --sandbox default --steps smartsearch-saved-themes.json
node scripts/webcheck.mjs --sandbox default --steps smartsearch-advanced-desktop.json
node scripts/webcheck.mjs --sandbox default --steps smartsearch-advanced-tv.json
node scripts/webcheck.mjs --sandbox default --steps smartsearch-advanced-phone.json
node scripts/webcheck.mjs --sandbox default --steps smartsearch-advanced-themes.json
node scripts/webcheck.mjs --sandbox default --steps smartsearch-header-desktop.json
node scripts/webcheck.mjs --sandbox default --steps smartsearch-header-tv.json
node scripts/webcheck.mjs --sandbox default --steps smartsearch-header-phone.json
node scripts/webcheck.mjs --sandbox default --steps smartsearch-header-themes.json
node scripts/webcheck.mjs --sandbox default --steps smartsearch-collections-desktop.json
node scripts/webcheck.mjs --sandbox default --steps smartsearch-collections-tv.json
node scripts/webcheck.mjs --sandbox default --steps smartsearch-collections-phone.json
node scripts/webcheck.mjs --sandbox default --steps smartsearch-collections-themes.json
node scripts/webcheck.mjs --sandbox default --steps smartsearch-collectionnote.json
./scripts/Stop-Sandbox.ps1 -Name default
```

| Webcheck | Checks | Mode |
| --- | --- | --- |
| `webchecks/smartsearch-page.json` (21 steps) | `index.html` carries one Smart Search tag and its head fragment before `</head>`, with the page `search` known and on; the kit registers it on its own (fallback) host at `#/jfss-search`; the drawer's *Library tools* links it as *Advanced Search*; the page renders there under that title with its search form (the *Title contains* field) and no *Page not found* frame, and again after a hard reload; `GET /SmartSearch/Fields` as `viewer`: 200, registry version 2, the 32 fields without the administrator-only `Path`, `Title` first, none with `Matches` (not offered yet), 15 sort keys and the six item kinds; no uncaught exception of ours (a sandbox's stale tag of another plugin is known noise). Screenshots `smartsearch-drawer.png`, `smartsearch-page.png` | `FallbackOnly` (the default) |
| `webchecks/smartsearch-pluginpages.json` (26 steps) | `GET /PluginPages/User` lists `Jellyfin.Plugin.SmartSearch.SearchPage` with its shell URL `/SmartSearch/Pages/search` and the title *Advanced Search*; the kit chose the Plugin Pages host and links the page there; the drawer shows Plugin Pages' entry and not ours; the page renders inside Plugin Pages' frame, and again after a hard reload of our link; `#/jfss-search` still renders it (the fallback route). Screenshots `smartsearch-pp-drawer.png`, `smartsearch-pp-page.png` | `Auto`, with Plugin Pages installed |
| `webchecks/smartsearch-search-desktop.json` (79 steps) | At 1366x900. As `admin` it gives two movies genres (*Blade Runner*: Science Fiction and Thriller; *Test Pattern*: Documentary; sandbox data, the same every run); then as `viewer` in a context of its own: the page opens with the six item types, Movies and Shows pressed, all libraries and the focus in the title field; a title search (`blade`, Enter) finds what `/Items` finds for that `SearchTerm` and writes `kinds`, `q` and `run` into the hash; Movies only with the years 2001 to 2001 finds what `/Items` finds for `Years=2001`; the library *Movies 2* finds what `/Items` finds under it (fewer than all movies) and writes `lib`; a year before 1800 shows its error under the field and sends nothing; typing `sci` in the genre field suggests *Science Fiction* (the value index after the edit), and a search with it finds what `/Items` finds for that genre; all movies sorted by Year, Descending, are newest first; the List view shows every movie as a row with its facts; Open goes to the details page and Back renders the same search from the hash; a saved query's link (`tab`, `query` and a search) runs it, a second such link (the hash only) renders its own search without a reload, Back the first again, and a hard reload of it renders it; Play starts the first result in this browser's player; no uncaught exception of ours. Screenshots `smartsearch-search-*.png` | any |
| `webchecks/smartsearch-search-tv.json` (24 steps) | The TV layout (`localStorage` `layout` = `tv`): the root carries `jfss-tv`, no field takes the focus, the basic fields are one column, nothing is wider than the screen or under 32 px high; a title search typed and started with Enter on the focused Search button finds what `/Items` finds, with Open and Play on every card and no pager; Enter on a focused Open goes to the details page. Screenshots `smartsearch-search-tv*.png` | any |
| `webchecks/smartsearch-search-phone.json` (22 steps) | 390x844 with touch and the mobile layout: nothing wider than the screen, the scope bar and the fields stacked, the item types, Search and Reset at least 40 px high; a title search's cards and the List view's rows within the screen, Open, Play and the view buttons at least 40 px. Screenshots `smartsearch-search-phone*.png` | any |
| `webchecks/smartsearch-search-themes.json` (39 steps) | A title search (`test`, Movies) in Dark, Light, Apple TV and Purple Haze (`<userId>-appTheme`, then a reload): the page's text is the theme's text colour, and the pressed item type and the Search button are the theme's accent. Screenshots `smartsearch-search-theme-*.png` | any |
| `webchecks/smartsearch-saved-desktop.json` (122 steps) | At 1366x900. As `admin`: `viewer2` limited to every library but *Movies 2*, the `WC ` queries of earlier runs removed, two shared queries saved (*WC Two libraries* over *Movies* and *Movies 2* with no rule; one named with markup, a right-to-left override and a control character). As `viewer`: the tabs; a title search saved through the save dialog (Enter with no name asks for one; a description; Share): the header names it and the hash holds its id; an edit turns Save on and Save changes it on the server; its link from the address bar, and after a hard reload, fills the form and runs it by id; Save as new makes a second, and Back and Forward between the two links render each; the Saved queries tab lists the user's own with Rename, Duplicate, Stop sharing, Copy link and Delete, and admin's with Duplicate and Copy link, *Shared by admin*; the odd name is text in a `bdi` (no element made, the control character as U+FFFD); find and the Mine filter go into the hash; rename, share (asked first) and a duplicate of admin's query; Copy link; delete (asked first) of the query open on the Search tab, which closes it. As `viewer2`: viewer's shared query by owner; *WC Two libraries* shows *1 library not available to you*, never *Movies 2*'s name or id anywhere in the page, opens in the advanced form (the basic form can't hold a library the user can't open), and finds exactly viewer2's movies of *Movies*, run by id. `AllowQuerySharing` off: viewer2's list loses others' shared queries; then on again. Screenshots `smartsearch-saved-*.png` | any |
| `webchecks/smartsearch-saved-tv.json` (26 steps) | The TV layout: the Saved queries tab reached with Enter on its tab, no Copy link, no field takes the focus, nothing under 32 px high or wider than the screen; Enter on Rename opens the dialog with the focus on its name, the D-pad moves inside it, Escape closes it and the focus is back on Rename. Screenshots `smartsearch-saved-tv*.png` | any |
| `webchecks/smartsearch-saved-phone.json` (26 steps) | 390x844 with touch and the mobile layout: the list, the tabs, filters, row actions and names at least 40 px and within the screen; an open query's Search, Reset, Save and Save as new within the screen and 40 px high; the save dialog within the screen. Screenshots `smartsearch-saved-phone*.png` | any |
| `webchecks/smartsearch-saved-themes.json` (39 steps) | The list in Dark, Light, Apple TV and Purple Haze: the page's text is the theme's text colour; the names (links), the selected tab's line and the pressed filter are the theme's accent. Screenshots `smartsearch-saved-theme-*.png` | any |
| `webchecks/smartsearch-advanced-desktop.json` (82 steps) | At 1366x900. As `admin`: synthetic metadata on a few movies and a show (genres, `wc-` tags, a studio, ratings, an original title, an overview, a premiere date, a lock, an IMDb id, the show's status), `viewer2` limited to every library but *Movies 2*, and played, favourite and resume states per user. With the keyboard alone (Enter on Advanced and Add rule, the arrows in the selects, Tab, typing): Year between 2001 and 2010 and a None-of group with a title rule, equal to `/Items`. An empty value is marked under its rule, focused, and nothing is sent. Then every field built through its own editor, one search each, compared with `/Items` for the same user: as `admin` and as `viewer2` (no `Path`; *Movies 2* not among the libraries). A saved advanced query (*WC Advanced*) opens in the advanced form, and a change saved keeps it. Screenshots `smartsearch-advanced-desktop.png`, `smartsearch-advanced-check.png` | any |
| `webchecks/smartsearch-advanced-tv.json` (27 steps) | The TV layout: a link holding a tree (a None-of group; a value of markup, a right-to-left override and a control character) opens in the advanced form and runs; `jfss-tv`, no control takes the focus, a rule's controls stacked, nothing under 32 px high or wider than the screen, the value as text in a `bdi`; Enter on Add rule focuses the new rule's field, the D-pad moves the focus between controls without changing them, Enter on Search searches, equal to `/Items`. Screenshots `smartsearch-advanced-tv*.png` | any |
| `webchecks/smartsearch-advanced-phone.json` (15 steps) | 390x844 with touch and the mobile layout: the same tree within the screen, a rule's field as wide as the rule, the tree's buttons and Basic \| Advanced at least 40 px high; a date range added by touch still fits. Screenshots `smartsearch-advanced-phone*.png` | any |
| `webchecks/smartsearch-advanced-themes.json` (47 steps) | The tree in Dark, Light, Apple TV and Purple Haze (`<userId>-appTheme`, a reload, then the link): the page's text is the theme's text colour, a nested group's edge and the pressed Advanced are the theme's accent, a rule's error has the form's error colour. Screenshots `smartsearch-advanced-theme-*.png` | any |
| `webchecks/smartsearch-header-desktop.json` (87 steps) | At 1366x900. As `viewer`: one *Advanced search* button right before jellyfin-web's search button in the header, its size and colour; a click and Enter (after Shift+Tab from the search button) open the page; still one, shown, on the page, at home, in a library, the user menu and after Back; signing out hides it with the search button (the login page); in the experimental layout none shows (jellyfin-web keeps the legacy header there, hidden) and nothing is reported. As `admin`: `ShowHeaderButton` off, then the page off, each leave no button after a reload, and both are put back; then, with the search button's class taken away as jellyfin-web builds it (a simulated missing anchor), no button, the page still on its route, one console warning, one health report and the server's Diagnostics listing it; no uncaught exception. Screenshots `smartsearch-header-*.png` | `FallbackOnly` |
| `webchecks/smartsearch-header-tv.json` (24 steps) | The TV layout: one button before the search button; the D-pad's ArrowLeft from the search button reaches it and ArrowRight goes back, and Enter on it opens the page; still one after Back. Screenshot `smartsearch-header-tv.png` | any |
| `webchecks/smartsearch-header-phone.json` (18 steps) | 375x812 with touch and the mobile layout: the button before the search button, at least 40 px square, every header button within the screen and no horizontal scroll; a tap opens the page. Screenshot `smartsearch-header-phone.png` | any |
| `webchecks/smartsearch-header-themes.json` (40 steps) | Dark, Light, Apple TV and Purple Haze: the button in the search button's colour and size in each. Screenshots `smartsearch-header-theme-*.png` | any |
| `webchecks/smartsearch-collections-desktop.json` (134 steps) | At 1366x900. As `admin`: the `WC AC` auto-collections (with their collections) and queries of earlier runs removed, `viewer2` allowed to manage collections, a query over *Movies* saved. The Auto-collections tab third, with New, Refresh all and the All, Needs review, Errors filter; New auto-collection: Save off until a preview, locked and keeping manual changes by default; the chosen query names it, the name is free, the preview counts and shows posters; Save makes it, *Up to date*, with a link to its collection (a locked BoxSet of that name), its query, members and last change, and Refresh, Edit, Turn off, History and Delete. A second New with the same name: taken, with free names to pick. Edit: unlocking leaves only *Release date* and waits for a new preview. Refresh now and its toast; History newest first; Turn off and on; the Errors filter; the Saved queries tab's *Create an auto-collection from …*. As `viewer2`: admin's auto-collection without Turn off, its name and query locked in the editor. As `viewer`: no tab, no create button. As `admin` again: Refresh now ends in a *+N −M* toast without a reload; the source query edited to match nothing, and the run that would empty the collection held (*Needs review*, its filter, Wait folds the panel and *Review removals* opens it, Apply asks first, *Keep these items* pins them and it is up to date), then the query restored; the collection deleted outside the plugin shows *Collection deleted* without a link after the event trigger's quiet minute, and Recreate makes it again; the source query refused to a plain delete, then removed by a forced import, shows *Query missing* with its last error and *Choose another query*. Delete keeps the collection by default; with *Delete the collection too* the BoxSet goes. Screenshots `smartsearch-collections-*.png` | any |
| `webchecks/smartsearch-collections-tv.json` (26 steps) | The TV layout, as `admin`, with *WC AC TV* made through the API: the tab reached with Enter on its tab, no field takes the focus, nothing under 32 px high or wider than the screen; Enter on Edit opens the editor with the focus on its name, the D-pad moves inside it, Escape closes it and the focus is back on Edit. Screenshots `smartsearch-collections-tv*.png` | any |
| `webchecks/smartsearch-collections-phone.json` (22 steps) | 390x844 with touch and the mobile layout, as `admin`, with *WC AC Phone*: the list, tabs, filter, row actions and names at least 40 px and within the screen; the editor takes the whole width, nothing in it wider than the screen. Screenshots `smartsearch-collections-phone*.png` | any |
| `webchecks/smartsearch-collections-themes.json` (42 steps) | *WC AC Themes* in Dark, Light, Apple TV and Purple Haze: the page's text is the theme's text colour; the names (links), an up-to-date status chip, the selected tab's line and the pressed filter are the theme's accent. Screenshots `smartsearch-collections-theme-*.png` | any |
| `webchecks/smartsearch-collectionnote.json` (52 steps) | At 1366x900, as `admin`: *WC AC Note* made through the API; its collection's details page has the note right above the collection's items (*Updates automatically from a saved search · updated …*) with Edit (the tab's editor on it) and Refresh now (*updating…*, then *updated just now* and a toast, a minute after it was made); a movie's page has none, and the collection's page again has one. As `viewer`: the text only; on a 390x844 phone it fits the screen. With `ShowCollectionNote` off there is none (put back on). Screenshots `smartsearch-collectionnote*.png` | any |

`Auto` takes effect at startup: set on a running server, the step that lists the page through Plugin Pages fails, so
restart the sandbox after `Set-SandboxPluginConfig.ps1`. Only `smartsearch-search-desktop.json` (the two movies' genres), `smartsearch-saved-desktop.json` (its `WC `
queries, `viewer2`'s libraries; it turns `AllowQuerySharing` off and on again) and `smartsearch-advanced-desktop.json`
(its metadata, `viewer2`'s libraries, the users' watch states and its `WC Advanced` query) and the
`smartsearch-collections-*.json` and `smartsearch-collectionnote.json` checks (their `WC AC` queries, auto-collections
and collections, removed at the end; the desktop one lets `viewer2` manage collections and takes that back, the note's
turns `ShowCollectionNote` off and on again) write, and
`smartsearch-header-desktop.json` turns `ShowHeaderButton` and `EnableSearchPage` off and puts them back. A
webcheck's sign-in leaves a *Jellyfin Web* device per run: revoke them afterwards (`DELETE /Devices?id=…`), never the
`jf-sandbox-<name>-<user>` devices `Initialize-Sandbox.ps1` made.

### Smart Search probes

Two probes check the engine against core's own `/Items` in a sandbox seeded by `Initialize-Sandbox.ps1` (any plugins
and community plugins; `-Community None` will do). Both share `smartsearch-probe-common.mjs`, print one JSON result
through `redact.mjs` (`--out` saves it) and exit 0 when it passed.

```powershell
node scripts/smartsearch-smoke-probe.mjs --sandbox default          # the quick release check, about 2 minutes
node scripts/smartsearch-differential-probe.mjs --sandbox default   # [--queries 200] [--seed <n>] [--no-seed-data] [--scan]
```

| Probe | Checks | Writes |
| --- | --- | --- |
| `smartsearch-smoke-probe.mjs` | A query with no rule over each library and item kind, and over all libraries, equals `/Items?ParentId=…&Recursive=true&IncludeItemTypes=…&IsMissing=false` for `admin`, `viewer` and `viewer2` (the ids, and a count no larger), three times: right after a scan, after a full metadata refresh of the first movie of *Movies* (which must be in `admin`'s results each time), and after a movie whose studio is new is reported added (`POST /Library/Media/Updated`): the new movie is found in its library and by its studio before any scan | The library *SS Smoke* over `.sandbox/<name>/smartsearch-smoke-probe/media` (no fetchers or savers; one movie to start with, since a scan skips an empty library folder) and the added movie there (each a copy of the seeded movie's video and a `movie.nfo`); both removed at the end, or by the next run after a killed one (`created.json`) |
| `smartsearch-differential-probe.mjs` | One query for each field, operator and value form `/Fields` lists, then `--queries` random ones (up to three rules, nested `Any` and negated groups, a library scope, a sort; `--seed`, 20261110 by default), with values from the items (exact values, boundaries, case and accent variants) and values no item has. Each runs as `admin`, `viewer` (no watch state) and `viewer2` (restricted for the run: no *Movies 2*, parental limit 13, the tag `ss-blocked` blocked); its result must equal `/Items` for that user evaluated in memory with the plugin's semantics, its count be no smaller than its results, and `admin`'s run with `ForceResidual` (the scope alone pushed down) find the same; an administrator-only field must be 403 for the others. A field or operator the probe can't evaluate fails it, so a new field needs the probe extended. The result counts queries, runs, paths and expected-empty runs, lists the coverage and every difference with its query | Unless `--no-seed-data`: synthetic metadata on 13 items in name order (the same values each run; a community rating of 0 on two of them, which `/Items` leaves out of its rows, so the probe reads which items store one from core's own filter `MinCommunityRating=0`; one studio name new each run, so it has no Studio item until a scan; `--scan` scans before the queries) after checking that no library saves metadata next to the media, and watch states for `admin` and `viewer2`. `viewer2`'s policy is saved to `.sandbox/<name>/smartsearch-differential-probe/saved-policy.json` and put back at the end, or by the next run |

## Tag Cloud checks

Tag Cloud (`plugins/TagCloud/DEVELOPMENT.md`) in a sandbox of its own, seeded by `Initialize-Sandbox.ps1`; it needs no
community plugin:

```powershell
./scripts/dev-server.ps1 -Name tc -Port 8112 -Plugins TagCloud -Community None -Background
./scripts/Initialize-Sandbox.ps1 -Name tc
node scripts/tagcloud-probe.mjs --sandbox tc [--keep-policy] [--out <file>]
node scripts/webcheck.mjs --sandbox tc --steps tagcloud-page.json --fail-on-exception
./scripts/Stop-Sandbox.ps1 -Name tc
```

`tagcloud-probe.mjs` seeds the sandbox's movies and series through its own API as admin, the same every run: two or
three tags each from a fixed pool (one with a casing of its own, so case is folded), one or two genres, a studio, two or
three actors, a director, a writer and a producer (a role the clouds don't count), all marked `tc-probe`, and one movie
tagged `tc-blocked`. It restricts `viewer2` to the Movies library with `tc-blocked` blocked (its policy saved in
`.sandbox/<name>/tagcloud-probe/viewer2-policy.json` and put back at the end, unless `--keep-policy`). Then it rebuilds
the index and checks:

- the people were read in one grouped read of the server's database (`PeoplePath` `Database` in
  `/TagCloud/Admin/Status`), with the build's time in the result;
- for admin, viewer and viewer2, every word count of every source over Movies, Shows and Both equals a count of core's
  own `/Users/{id}/Items` as that user (library access, the blocked tag), and so does the title count;
- the library picker (viewer2: Movies only), a library filter, narrowing by two words, a word of titles viewer2 may not
  see (ignored as a filter, an empty titles page), a word's titles against core's, a cloud turned off (403 with the kit's
  `feature-disabled` type, gone from the capabilities), and 401 without a user;
- a policy change after the build: `tc-probe` added to viewer2's blocked tags, and viewer2's next Tags cloud equals
  core's again with the index's generation unchanged (no rebuild), then the policy put back and the counts with it;
- a patch through the library-change monitor: one title's tags edited, the index's generation up and the new tag counted
  after the 60 s quiet period (put back after);
- saved clouds: a look kept, a shared cloud listed to another user and a private one not, 403 for another's update and
  delete, 409 for a name taken and for a save over a newer one, a duplicate as a private copy, an admin's delete of a
  shared cloud, none left behind.

It writes `.sandbox/<name>/tagcloud-probe/result.json` and exits 1 on any failed check.

`webchecks/tagcloud-page.json`, as `viewer` after the probe: the Library tools link, the packed cloud drawn, a word's
titles listed with the selection in the hash, a right-click's narrowing chip and Show all, the words slider to 25 (its
range 25 to 1,000 in steps of 25), the settings panel switching to Rows and back, a cloud saved under a name and shown
as the current one, the full-screen view covering the window with the cloud alone, its exit button fading when idle, Esc
closing it, and no uncaught exception. It deletes the cloud it saved and the look it left in `localStorage`.
Screenshots: `tagcloud-page.png`, `-titles`, `-settings`, `-show`.

## Release and install tooling

### Releasing a plugin

The plugin repository is `manifest.json` on `main`, which Jellyfin reads from
`https://raw.githubusercontent.com/spooneyryan/jellyfin-plugins/main/manifest.json`. A version's zip is the asset of the
GitHub release of its tag `<plugin folder in lower case>/v<x.y.z>` (`bookmarks/v1.2.2`); the catalog's `sourceUrl` is
`https://github.com/spooneyryan/jellyfin-plugins/releases/download/<plugin>%2Fv<x.y.z>/<plugin>_<x.y.z.0>.zip`. Tested on
Windows with Jellyfin 10.11.5.

**Two lines.** Each plugin is released for Jellyfin 10.11 from `build.yaml` (1.x, `net9.0`) and for Jellyfin 12 from
`build.12.yaml` (2.x, `net10.0`, built with `-p:JellyfinTarget=12`). Both lines' versions sit in the one catalog, each with
its `targetAbi`. A 10.11 server is offered only 1.x (a `targetAbi` of 12.4 is newer than it); a 12 server is offered
every version, since Jellyfin sets no minimum, and takes the highest, so the 12 line's numbers stay above every 1.x. A release of the 12 line is the same procedure with
`build.12.yaml` in step 1 and `-JellyfinTarget 12` in steps 2 and 4 (`-f jellyfin_target=12` for the run by hand); its tag
is `<plugin>/v2.x.y`, and the workflow takes the line from the build file that says the tag's version. To check
references against a Jellyfin 12, pass `-JellyfinDir` with a 12.x program folder (the sandbox's
`artifacts\jellyfin-12.4-win\jellyfin`); `Test-ReleaseCatalog.ps1 -JellyfinDir` with one rehearses the 12 line.

1. **Version and notes.** Set `version: "x.y.z.0"` in `plugins/<Plugin>/build.yaml` and put an `x.y.z` section at the
   top of its `changelog`: that section is the version's notes in the catalog and on the GitHub release. Commit.
2. **Package.** `./scripts/Package-Release.ps1 -Plugin <Plugin>` builds the committed sources and writes
   `artifacts/release/<plugin>_<x.y.z.0>.zip` (with `.md5` and `.notes.md`), and adds the version to `manifest.json`;
   other versions' entries stay.
3. **Rehearse** (recommended): `./scripts/Test-ReleaseCatalog.ps1 -Port <port> -ServePort <port> -Plugin <Plugin>`
   installs it from a local copy of the catalog into a fresh sandbox (below).
4. **Commit** `manifest.json` ("Catalog: <Name> x.y.z") on `main`. Optionally, before tagging, check that GitHub builds
   the same zip: push that commit to a branch and run the workflow by hand on it (Actions, CI, "Run workflow", choose
   the plugin; or `gh workflow run ci.yml --ref <branch> -f plugin=<Plugin>`). That run only packages the plugin as the
   tag would and checks it against `manifest.json`; it releases nothing, and keeps the zip and its `.sha256` as the
   run's artifact.
5. **Tag** that commit: `git tag <plugin>/vx.y.z`.
6. **Push the tag alone**: `git push origin <plugin>/vx.y.z`. The tag carries the commit to GitHub, but `main` there,
   which is what Jellyfin reads the catalog from, doesn't move yet, so the catalog never lists a version whose zip
   isn't published. Push one tag per command: GitHub creates no push events (no workflow runs) when more than three
   tags are pushed at once.
7. **Wait for the release.** The tag's workflow run (`.github/workflows/ci.yml`, job `release`) runs the tests, rebuilds
   the zip with `Package-Release.ps1 -Tag <tag> -Verify` (it fails unless the MD5 is the one in `manifest.json`),
   creates the release `<Name> x.y.z` with the zip and the notes, then downloads the catalog's `sourceUrl` the way
   Jellyfin does and requires HTTP 200 and that MD5.
8. **Push `main`** to that commit once the run is green: `git push origin main`.

**If the run fails:**

- *Before the release exists* (tests, or `-Verify`'s MD5 differs, say from another SDK or compiler): the failed run
  keeps its zip and `<zip>.sha256` (the SHA-256 of each file in it) as the run's artifact (`gh run download <run id>`);
  compare that with `artifacts/release/<zip>.sha256` from step 2 to see which file differs. Either fix the cause and
  rerun the job, or publish the zip from step 2 yourself:
  `gh release create <plugin>/vx.y.z artifacts/release/<zip> --verify-tag --title "<Name> x.y.z" --notes-file artifacts/release/<zip>.notes.md`.
  Then check what Jellyfin will get: download the `sourceUrl` from `manifest.json`
  (`Invoke-WebRequest <sourceUrl> -OutFile $env:TEMP\check.zip`) and compare
  `(Get-FileHash $env:TEMP\check.zip -Algorithm MD5).Hash` with its `checksum` (case doesn't matter). Then step 8.
- *The release was created but the download check failed* (the job's last step): `main` hasn't moved, so no one is
  offered the version. A transient download error: rerun the failed job; it uploads the zip again with `--clobber`. A
  `sourceUrl` that doesn't serve the asset: fix how `Package-Release.ps1` writes it, then `gh release delete <tag> --yes`,
  delete the tag on GitHub and locally (`git push origin :refs/tags/<tag>`, `git tag -d <tag>`), and start again at
  step 2 on a new commit. A different `browser_download_url` alone is only a warning: the job checks the URL Jellyfin
  uses.

Fixing the catalog later (a typo in an overview) is a commit of `manifest.json` to `main`; the zip and its checksum
stay. Optional hardening, not done yet: pin the workflow's actions (`actions/checkout`, `actions/setup-dotnet`,
`actions/setup-node`) to commit SHAs instead of `@v5`.

### Package-Release.ps1

```powershell
./scripts/Package-Release.ps1 [-Plugin <names>] [-AllowDirty]           # zips, and manifest.json updated
./scripts/Package-Release.ps1 -Tag bookmarks/v1.2.2 -Verify             # the release workflow's check; writes no manifest
./scripts/Package-Release.ps1 -BaseUrl http://127.0.0.1:8115 -OutDir artifacts/release-test   # a test catalog
./scripts/Package-Release.ps1 -Plugin Bookmarks -JellyfinTarget 12 -JellyfinDir <a Jellyfin 12.x folder>   # the 12 line
```

`-JellyfinTarget` (10.11 or 12) picks the build file (`build.yaml` or `build.12.yaml`) and the build's line. Without it:
with `-Tag`, the line whose build file says the tag's version; otherwise the line of the Jellyfin in `-JellyfinDir`;
else 10.11. A default `-JellyfinDir` of the other line skips the reference check with a warning.

Every `plugins/*/build.yaml` plugin by default. Each is built in Release (`dotnet publish`, deterministic, the
checkout's path mapped to `/_/`, no git or source-link data, so the build depends on neither the checkout's folder nor
its commit) and checked like `Deploy-Plugin.ps1` does: only build.yaml's DLLs, the file version is build.yaml's, and,
when `-JellyfinDir` (default the installed server's program folder, only read; skipped in CI) holds Jellyfin 10.11, no
reference newer than it ships. It refuses uncommitted changes in the plugin's folder, `kit/` and the root build files
unless `-AllowDirty`, a version that isn't `x.y.z.0`, a changelog without an `x.y.z` section and a missing `image.png`,
and warns when `dotnet --version` isn't `RELEASE_SDK` in `ci.yml`.

**The same bytes on every machine.** Besides the path map, the build is pinned to the runtime the SDK ships with
(`DOTNET_ROLL_FORWARD=Disable`, no build servers). The portable PDB records the version of the runtime the compiler ran
on, and the DLL records the PDB's checksum; by default .NET rolls a program forward to the newest installed patch of its
runtime, so on a machine (or CI runner) with a newer .NET 10 runtime installed the same SDK would give a different DLL
and PDB. The script prints the compiler's and that runtime's versions, read back from the PDB, and fails if the runtime
isn't the SDK's own. It prints the SHA-256 of each entry and of the zip and writes them to `<zip>.sha256`.

**The zip** is flat, as Jellyfin's installer expects: it downloads the zip, checks its MD5 against the catalog's
`checksum`, extracts it into `plugins/<name>_<version>` and writes `meta.json` from the catalog, keeping what the zip's
`meta.json` already says. The entries, in name order:

| Entry | |
| --- | --- |
| `Jellyfin.Plugin.<Name>.dll`, `.pdb` | build.yaml's artifacts and their PDBs |
| `image.png` | `plugins/<Plugin>/image.png`, the Plugins page's card |
| `meta.json` | build.yaml's category, description, guid, name, overview, owner, targetAbi and version, the version's changelog section, `status: Active`, `autoUpdate: true`, `imagePath: image.png` and `assemblies` (the DLLs Jellyfin loads); no timestamp, so Jellyfin takes the catalog's |

The entries are stored, not compressed, with one fixed date, so the same build gives the same zip byte for byte in
either PowerShell. Jellyfin also downloads the catalog's `imageUrl` (`plugins/<Plugin>/image.png` on `main`) over the
zip's `image.png`.

**manifest.json** is Jellyfin's repository format: per plugin `guid`, `name`, `description`, `overview`, `owner`,
`category`, `imageUrl` and `versions` (newest first), each with `version`, `changelog`, `targetAbi`, `sourceUrl`,
`checksum` (MD5) and `timestamp`. The run adds or replaces the packaged versions; a rerun with the same checksum keeps
the timestamp, so rerunning changes nothing. Every run also writes `release.json` (what was packaged) next to the zips.

### Test-ReleaseCatalog.ps1

```powershell
./scripts/Test-ReleaseCatalog.ps1 -Port 8114 -ServePort 8115 [-Name catalog] [-Plugin <names>] [-AllowDirty]
```

The catalog end to end, the way an admin installs: `Package-Release.ps1 -BaseUrl` writes a test catalog in
`artifacts/release-test`, `serve-release.mjs` serves it on `127.0.0.1:<ServePort>`, `dev-server.ps1 -Plugins None
-Community None -Fresh` starts an empty sandbox, and the script completes the wizard (credentials in the sandbox's
`credentials.json`, never printed), adds the catalog as the only repository, installs each plugin through
`POST /Packages/Installed/<name>` (download, MD5 check, extraction), restarts the server through the API and checks that
each plugin is Active with its version and image, and that its folder holds the zip's files. The sandbox and the file
server are stopped at the end; the result is printed and saved as `artifacts/release-test/catalog-test.json`.

### Install-RealServer.ps1

Optional tooling: installs a plugin's release on your own Jellyfin server from a clone of this repository.

```powershell
./scripts/Install-RealServer.ps1 -Plugin Bookmarks -Version 1.2.1 -WhatIf     # the plan; changes nothing
./scripts/Install-RealServer.ps1 -Plugin Bookmarks -Version 1.2.1 [-DataDir <dir>] [-JellyfinDir <dir>] [-BackupRoot <dir>]   # without -Version: the newest tag
./scripts/Install-RealServer.ps1 -Plugin Bookmarks -DataDir <a stopped sandbox's data folder> -BackupRoot <dir> -FromWorkingTree [-NoBuild]   # rehearsals only
```

User-run only, after the sandbox checks (for Bookmarks, `plugins/Bookmarks/DEVELOPMENT.md`, "Installing and
upgrading"). It never starts, stops or calls a server: exit Jellyfin from its tray icon first. Pass the version the
`-WhatIf` run showed, so a tag made in between can't change what is installed.

| Option | Effect |
| --- | --- |
| `-Plugin` | the plugin folder's name, in any case |
| `-Version x.y.z` | the release tag to install; default the newest |
| `-DataDir` | the Jellyfin data folder (default the real server's `C:\ProgramData\Jellyfin\Server`) |
| `-JellyfinDir` | the Jellyfin program folder Deploy's checks read (default the installed one, only read) |
| `-BackupRoot` | where the cold backup goes (default `~\JellyfinBackups`) |
| `-FromWorkingTree` | rehearsal: build the working tree (with `-NoBuild` its last Release build); refused for the real server |
| `-WhatIf` | runs the checks and prints the plan |

**What is installed: the plugin's release tag, never the working tree.** Release tags are `<plugin folder in lower
case>/v<major>.<minor>.<patch>` (`bookmarks/v1.1.1`). Without `-Version` it takes the newest by number (`v1.10.0` over
`v1.9.0`) and leaves other shapes out (`v1.2`, `v1.2.0-rc1`). Before anything is backed up or made it refuses when
there is no such tag or two tags name the version, when the tag's commit is on no branch, when that commit's
`plugins/<Plugin>/build.yaml` doesn't say the tag's version as `x.y.z.0` (`bookmarks/v1.0.0` predates this repository's
layout and can't be installed this way), or when the temporary worktree's folder is taken (by a folder or a registered
worktree, also one whose folder is gone until `git worktree prune`); it never deletes or reuses one.

**The scripts run from the working tree**, so for the real server it refuses unless the checkout's PowerShell scripts
(`scripts\*.ps1`, `*.psm1`, `*.psd1`, subfolders too) are its HEAD's: nothing changed, staged, added, assume-unchanged
or skip-worktree (`Get-RepoScriptsProblem`). Node probes don't count. The message says what to commit or put aside, or
to run from a fresh worktree of main (`git -C <checkout> worktree add --detach <folder> main`). A stopped sandbox's data
folder accepts changed scripts: that is how they are rehearsed.

In this order:

1. **Checks.** `-DataDir` must be a Jellyfin data folder (`config` and `data` in it); no `jellyfin.exe` may use it (its
   `--datadir`, or its program folder inside the data folder; one whose command line can't be read counts); the backup
   may be neither under `C:\ProgramData\Jellyfin` nor inside the data folder. `-DataDir`, `-BackupRoot` and
   `-JellyfinDir` are resolved through the script's own PowerShell drives first. `-FromWorkingTree` is refused whenever
   `-DataDir` is the real server's in any spelling the path guard knows (`Test-RealServerTarget`; also a drive letter
   Windows doesn't know), so its default `-DataDir` is refused too. `-NoBuild` without `-FromWorkingTree`, and
   `-Version` with it, are refused. Then the plan, then the tag is checked out next to the repository at a short path
   (`C:\src\jfp-rel-<plugin>-<x.y.z>` for a checkout in `C:\src\jellyfin-plugins`), detached and locked with the run's
   own reason (`git worktree add --lock --reason "Install-RealServer.ps1 run <id> (PID …, started …)" --detach`).
2. **A cold backup** to `<BackupRoot>\<yyyyMMdd-HHmmss>-before-<Plugin>-<version>`: `config`, `root`, `plugins` (with
   `plugins\configurations`), every file directly in `data` (the databases with `-wal` and `-shm`, `device.txt`, ...;
   not `*.old`, `*.tmp` or `*.bak*`) and the folders `collections`, `playlists`, `ScheduledTasks`, `introskipper` and the
   plugin's own (`data\bookmarks`) when there; not `metadata`, `cache`, `log`, transcodes, trickplay, subtitles,
   attachments or `SQLiteBackups`. Every item is counted and measured in both places; a difference stops the script
   before anything is installed. `backup.json` lists the items, the `tag` and `commit` installed (`null` with
   `-FromWorkingTree`) and, once installed, `installed`: the plugin folder and each file's `name`, `sha256`, `bytes`
   (`image.png` among them), and `image`: where the image came from (`source` `Tag`, `Head` or `WorkingTree`, its
   `commit`, `from` in words, `sha256`, `bytes`), or `null` without one; `RESTORE.txt` says it too (`Its image, ...`).
   `RESTORE.txt` says what was installed and gives two ways back:
   - **A** removes the plugin alone and keeps everything the server recorded since: delete the plugin's new folder and
     put back the earlier version's folder, delete or replace its data folder `data\<plugin>` and its settings file
     `plugins\configurations\<assembly>.xml`, and set back other plugins' settings changed along with it (on their
     pages, or by copying only their files, never the whole `plugins\configurations`).
   - **B** copies `config`, `root`, `plugins` and `data` back, with a warning that this puts `data\jellyfin.db` back as
     it was and loses everything recorded since (watched states, positions, users, library changes): only for a broken
     server.
   - For Access Roles, **A** starts with a step 0, *Release all locks* with *Also restore each user's original library
     access* ticked while the plugin still runs, and both ways move `data\accessroles` aside instead of deleting it
     (`Rename-Item -LiteralPath '<DataDir>\data\accessroles' -NewName ('accessroles.removed-' + (Get-Date -Format
     yyyyMMdd-HHmmss))`), then copy the backup's back when it had one. The folder moved aside keeps the ledger and the
     `PreferenceMirror` records that `plugins/AccessRoles/DEVELOPMENT.md`'s "Recovery after the plugin folder was
     deleted with the mirror on" reads.
3. **The install**: `Deploy-Plugin.ps1 -RealServer -SourceRoot <the tag's worktree> -ReleaseLock <its lock> -PassThru`, a
   Release build of the tag checked as described under "Deploy-Plugin.ps1"; the hashes go into `backup.json`,
   `RESTORE.txt` and onto the host, and the build is left in `artifacts\deploy\<Plugin>\Release` for `-NoBuild`.
4. **Clean-up.** The worktree is always removed (`git worktree unlock`, `git worktree remove --force`, up to three
   tries, then `git worktree prune`), also after a failure; what can't be removed is reported with the commands that
   clear it. Only a worktree locked with this run's reason is removed. Git runs in processes of its own (`Invoke-Git`),
   so Windows PowerShell 5.1 doesn't turn git's progress into errors.

`-WhatIf` prints the release (`Release: bookmarks/v1.1.1, commit <hash>… (on main), Bookmarks 1.1.1.0 by its
build.yaml, the newest of 3 release tags`), where it would be built, the image it would install (`Image: image.png from
bookmarks/v1.1.1 (commit …)`, or from the committed HEAD when the tag has none), what would be backed up and installed, and changes
nothing (its git commands only read).

**A refusal is on stdout too**: `Refused: <reason> Nothing was backed up or installed.`, or after the backup `Stopped:
the install failed after the backup: <reason> The backup is in <folder>; its RESTORE.txt gives the ways back.` To keep
a run's output, capture both streams: `./scripts/Install-RealServer.ps1 ... *>&1 | Tee-Object <file>`, or
`powershell.exe -File scripts/Install-RealServer.ps1 ... > <file> 2>&1`; the exit code (1) still says it failed.

Repo.Tests covers it without a real server: `SandboxToolingTests` on a synthetic data folder (the backup, `backup.json`,
`RESTORE.txt`, `-WhatIf`, the refusals, Access Roles' way back), `ReleaseInstallTests` and `ReleaseInstallReviewTests`
in a temporary git repository with a synthetic plugin and tags (tag order, `-Version`, the refusals, the real server's
path in many spellings, the install from the tag, the worktree removed, the lock, the SHA-256 records, a build swapped
or planted during the install), and `PluginImageTests` (the image from the working tree, the tag or HEAD, its records).

## Troubleshooting

| Symptom | Cause and fix |
| --- | --- |
| `dev-server.ps1` refuses: a clip path outside the sandbox | A `bookmarks.json*` or `ClipOutputPath` names a folder outside `.sandbox/<name>` (a fixture or store copied from elsewhere, or a probe killed while `ClipOutputPath` pointed at a missing drive). Rewrite the paths into the sandbox's clip folder or move the files aside (see "The 1.0.0 store"); put `ClipOutputPath` back in the settings XML. |
| `dev-server.ps1` refuses: port 9 in use | Something listens where the dead proxy points; stop it. |
| `dev-server.ps1` refuses: the port is in use | Another sandbox (or a stale server) uses it: `Stop-Sandbox.ps1 -Name <it>`. |
| Deploy fails: a reference newer than the server ships | Pin the package in `Directory.Packages.props` to the server's version; or `-JellyfinDir` doesn't match `-JellyfinTarget`. |
| Deploy's file-version check stops a `-NoBuild` start | `artifacts\deploy` holds a build of another version than the checkout's `build.yaml`; build again without `-NoBuild`. |
| `-NoBuild` deployed Debug over a Release sandbox | Start once with `-Configuration Release -NoBuild`; `sandbox.json` remembers it. |
| `Initialize-Sandbox.ps1` fails on a download task | A trigger appeared (restart with `dev-server.ps1`, which empties it) or a Jellyfin or JE update renamed a task (update the task table in `JellyfinSandbox.psm1`). |
| A `je` sandbox fails the fixture check | It started once without the fixture: recreate it with `dev-server.ps1 -Name je -Fresh`. |
| `/api-docs/openapi.json` answers 500 | A schema-id collision between plugins; `api-docs-probe.mjs` names both types (see "api-docs-probe.mjs"). |
| A probe stopped half way | Run it again: it first undoes what its state folder records (moved media, folders renamed aside, settings). A probe that says to fix something by hand names the folder. |
| `Stop-Sandbox.ps1` reports "still running" | The server is slow to exit: run it again or raise `-ExitTimeoutSec`. It never stops a process by name. |
| A webcheck fails on the first run after a restart into Plugin Pages' `Auto` mode | Plugin Pages registers a few seconds after "Startup complete"; run it again. |
| A `--goto /web/...` from Git Bash is refused | Git Bash rewrote the path: set `MSYS_NO_PATHCONV=1`. |
| A `reload` after a hash-only `goto` loads the previous route | Such a `goto` returns at once; `waitFor` the new hash first (`location.hash === '#/…'`). |
| A caller of `dev-server.ps1 -Background` hangs on its pipes | Only with `Start-Process -Redirect…`; the script's own start leaves a caller's pipes alone (see "dev-server.ps1 and Stop-Sandbox.ps1"). |
| Out of memory, slow scans | Run one sandbox at a time and stop it when done; the data webchecks and the JE import probe run with `node --max-old-space-size=2048`. |
