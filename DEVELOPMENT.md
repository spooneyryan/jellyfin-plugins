# Jellyfin plugins

A suite of Jellyfin **10.11.5** server plugins that share a source-only kit. Each plugin's `README.md` is its user
guide and its `DEVELOPMENT.md` the developer documentation (design, data, API and tests).

| Plugin | Folder | Version | What it does |
| --- | --- | --- | --- |
| Access Roles | `plugins/AccessRoles` | **1.0.3** | Roles that keep non-admin users out of settings, the Dashboard's data and libraries they may not open ([README](plugins/AccessRoles/README.md), [DEVELOPMENT](plugins/AccessRoles/DEVELOPMENT.md)) |
| Bookmarks | `plugins/Bookmarks` | **1.2.2** | Named per-user bookmarks, the My Bookmarks and My Clips pages, and ffmpeg clip extraction ([README](plugins/Bookmarks/README.md), [DEVELOPMENT](plugins/Bookmarks/DEVELOPMENT.md), [CHANGELOG](plugins/Bookmarks/CHANGELOG.md)) |
| Library Maintenance | `plugins/LibraryMaintenance` | **1.0.4** | Find duplicate movies across your libraries, and preview moving media ([README](plugins/LibraryMaintenance/README.md), [DEVELOPMENT](plugins/LibraryMaintenance/DEVELOPMENT.md), [CHANGELOG](plugins/LibraryMaintenance/CHANGELOG.md)) |
| Smart Search | `plugins/SmartSearch` | **1.1.2** | An Advanced Search page with rules over title, year, library and more, saved queries, and auto-collections ([README](plugins/SmartSearch/README.md), [DEVELOPMENT](plugins/SmartSearch/DEVELOPMENT.md)) |
| Tag Cloud | `plugins/TagCloud` | **1.0.1** | A word cloud of the library's tags, genres, people and studios, counted for each viewer, with saved clouds and a full-screen view ([README](plugins/TagCloud/README.md), [DEVELOPMENT](plugins/TagCloud/DEVELOPMENT.md)) |
| User Ratings | `plugins/UserRatings` | **1.0.2** | Five-star ratings with half stars and reviews, shared by every member of the server ([README](plugins/UserRatings/README.md), [DEVELOPMENT](plugins/UserRatings/DEVELOPMENT.md)) |

## Layout

- `Directory.Build.props`: `net9.0`, nullable, warnings as errors, `AnalysisMode=AllEnabledByDefault` and the StyleCop,
  Serilog and multithreading analyzers for every non-test project. `Directory.Packages.props`: central package
  versions; project files carry none.
- `kit/`: the shared kit, compiled into each plugin rather than shipped as a DLL ([kit/README.md](kit/README.md) shows
  how a plugin uses it and documents every part):
  - `build/`: `Kit.props` for plugin projects, `TestSupport.props` for test projects.
  - `src/`: the server side. `Api` (ProblemDetails errors, `CurrentUser`, the strict query binders), `Hosting` (options,
    the ABI guard, the startup gate, background services, the daily maintenance), `Web` (the `index.html` injection, the
    built client scripts, page hosting), `Diagnostics` (web anchors, ClientHealth, the admin Diagnostics), `Storage` (the
    JSON store, snapshots, export and import), `Sqlite` (only with `KitUseSqlite`), `Library` (item identity, the move
    journal, library changes, the record lifecycle, scoped library reads), `Users` (deleted users and the reconciler)
    and `Interop` (Jellyfin Enhanced, Custom Tabs and Plugin Pages, read-only).
  - `web/`: `kit.js`, `kit.css`, the kit's `anchors.json` and the Node tests (`node --test kit/web/tests/*.test.mjs`).
  - `testsupport/`: compiled into every test project: the plugin identity and API checks, the JSON store conformance
    suite, `WebHostHarness`, the web script checks, `FakeLibrary` and small helpers.
  - `testdata/`: the pinned schema-id and controller-name lists and synthetic fixtures (Jellyfin Enhanced data, the `je`
    sandbox settings, the move journal, `index.html`).
  - `tests/Kit.Tests`: tests `src/` and `testsupport/`. `VERSION`: the kit's own version.
- `plugins/<Name>/`: one folder per plugin with its `build.yaml`, project, tests, `README.md`, `DEVELOPMENT.md` and
  `image.png` (its card on the Dashboard's Plugins page, which `Deploy-Plugin.ps1` installs with it).
- `branding/`: the sources of the plugins' images and the script that renders them ([branding/README.md](branding/README.md)).
- `scripts/`: build, deploy and sandbox tooling for every plugin ([scripts/README.md](scripts/README.md) documents every
  script, probe and webcheck, and the safety rules):
  - `Deploy-Plugin.ps1` (with a reference-version check: no assembly newer than the target Jellyfin ships),
    `dev-server.ps1`, `Stop-Sandbox.ps1`, `Initialize-Sandbox.ps1`, `New-SandboxMedia.ps1`, `Set-SandboxPluginConfig.ps1`;
  - the headless browser checker `webcheck.mjs` with `webchecks/*.json`, `index-probe.mjs`, `compare-snapshots.mjs`,
    `Test-WebAnchors.ps1`, `api-docs-probe.mjs`, and the plugins' and the kit's sandbox probes (`*-probe.mjs`);
  - `Package-Release.ps1`: the release zips (DLL, PDB, `image.png`, `meta.json`), their MD5 and the catalog
    `manifest.json`; `Test-ReleaseCatalog.ps1` installs every plugin from a local copy of the catalog
    (`serve-release.mjs`) into a fresh sandbox ([the release procedure](scripts/README.md#releasing-a-plugin));
  - `Install-RealServer.ps1`, run by hand: it installs only a plugin's release tag `<plugin>/v<x.y.z>`, built in a
    temporary worktree and checked by SHA-256, after a cold backup with `backup.json` and `RESTORE.txt`, and refuses
    while the server runs;
  - sandboxes never download: Jellyfin's "Update Plugins" and Jellyfin Enhanced's download tasks have no triggers there,
    the `je` fixture keeps JE's outbound features off, and the sandbox server runs behind a dead proxy.
- `tests/Repo.Tests`: repository-wide checks: `PluginIdentityTests` (each `build.yaml` against source, project and
  assembly), `KitAdoptionTests` (the ABI guard, `[KitAbiGate]` and `[PluginExceptionFilter]`, the kit's base classes for
  background services, post-scan and maintenance tasks, owners and consumers added through the kit),
  `ApiConventionCoverageTests` and `WebConventionCoverageTests` (every plugin runs the kit's API, store, script, anchor
  and dashboard checks), `KitUsersCoverageTests`, `JeSandboxFixtureTests`, `ScriptsTests`, `SandboxToolingTests`,
  `ServerPackagePinTests` (the server packages pinned to what Jellyfin 10.11.5 ships), `ReleaseInstallTests` and
  `PublicContentGuardTests`.
- `manifest.json`: the plugin repository's catalog, which Jellyfin reads from
  `https://raw.githubusercontent.com/spooneyryan/jellyfin-plugins/main/manifest.json`; each version's zip is the asset of
  the GitHub release of its tag `<plugin>/v<x.y.z>`.
- `.github/workflows/ci.yml`: build and every test on Windows for pushes and pull requests; a pushed release tag also
  packages that plugin, checks its zip against `manifest.json` and publishes it as the tag's GitHub release. Run by
  hand (workflow_dispatch), it only packages a chosen plugin and checks it, keeping the zip as the run's artifact.
- `.sandbox/`: local sandbox servers, their media and credentials (gitignored).

## Building

```powershell
dotnet build jellyfin-plugins.slnx
dotnet test jellyfin-plugins.slnx
```

The test projects roll forward to newer runtimes, so they run without the ASP.NET Core 9 runtime.

## Trying the plugins in a sandbox

They need Windows and a local Jellyfin 10.11.5 install, the .NET SDK, Node.js 22 and Chrome or Edge: see the
prerequisites in [scripts/README.md, "Quick start"](scripts/README.md#quick-start).

Sandboxes run the installed `jellyfin.exe` against folders under `.sandbox/<name>`, on loopback ports 8097–8100,
and never touch the real server in `C:\ProgramData\Jellyfin`:

```powershell
./scripts/Initialize-Sandbox.ps1 -Name default     # builds, deploys, starts, seeds media, users and libraries; download and /api-docs checks
node scripts/index-probe.mjs --sandbox default
node scripts/webcheck.mjs --sandbox default --steps bookmarks-ui.json
node scripts/webcheck.mjs --sandbox default --steps bookmarks-phone.json
node scripts/webcheck.mjs --sandbox default --steps kit-boot.json
./scripts/Set-SandboxPluginConfig.ps1 -Name default -Plugin Bookmarks -Set @{ EnablePageDemo = $true }
node scripts/webcheck.mjs --sandbox default --steps pages-fallback.json  # page hosting, see scripts/README.md
node scripts/webcheck.mjs --sandbox default --steps health-quiet.json    # no web anchor missing
./scripts/Test-WebAnchors.ps1                                            # our and Plugin Pages' anchors in the installed web client
./scripts/Stop-Sandbox.ps1 -Name default
```

`scripts/README.md` covers the four standard sandboxes, every script and the safety rules.

## Licence

GPL-3.0-only (the GNU General Public License, version 3), the licence of the Jellyfin packages the plugins build against. See `LICENSE`.
