# `je` sandbox fixture

Pinned settings for the `je` sandbox, which runs our plugins next to Jellyfin Enhanced (JE), Plugin Pages, Custom Tabs
and JavaScript Injector (4.0 when the fixture was written, also checked with 4.1.0.0; `dev-server.ps1` copies the newest
version folder of the installed server's copy), set up the way a server that uses them together would be. Coexistence
checks are only meaningful against these settings, not against JE's defaults. The fixture was written against JE 12.8;
`dev-server.ps1` copies the newest JE folder of the installed server, and the kit was checked with 12.11, 12.12 and
12.14. These keep every setting name, shortcut and default the fixture pins (`kit/README.md`, "Jellyfin Enhanced 12.11"
and "Jellyfin Enhanced 12.12"; 12.14 through the read-back), so the same files and `expected.json` serve them all
(`Initialize-Sandbox.ps1` read them back from 12.12 and from **12.14.0.0**).

| File | Copied to (before the first start) | Content |
| --- | --- | --- |
| `plugins/configurations/Jellyfin.Plugin.JellyfinEnhanced.xml` | `.sandbox/je/data/plugins/configurations/` | JE settings: user reviews on, bookmarks on with Plugin Pages and Custom Tabs hosts, and the shortcut list including `BookmarkCurrentTime` = `B` |
| `plugins/configurations/Jellyfin.Plugin.PluginPages/config.json` | same | JE's "Bookmarks" page entry |
| `plugins/configurations/Jellyfin.Plugin.CustomTabs.xml` | same | JE's "Bookmarks" Custom Tab |
| `expected.json` | not copied | the values `Initialize-Sandbox.ps1 -Name je` reads back through the running sandbox; it fails when any differ. Also the plugins that must be Active: JE, Custom Tabs and, when the sandbox has it, JavaScript Injector (installed with no settings of its own: no script, just its `index.html` middleware) |

Rules:
- These files are hand-written. Never copy a server's own XML, JSON or data files into this folder or into a
  sandbox: they hold user ids, tokens and personal data.
- The shortcut names and keys were checked read-only against an installed JE's settings (names and keys only); the
  labels are our own wording.
- `dev-server.ps1 -Name je` applies the fixture automatically on the sandbox's first start
  (`Initialize-Sandbox.ps1 -Name je -PreStart`). A `je` sandbox that already started without it must be recreated
  with `-Fresh`.
- Keep `expected.json` and the XML in step; `tests/Repo.Tests/JeSandboxFixtureTests.cs` checks that they agree, and
  `SandboxToolingTests` that its plugin ids are those of the scripts' community-plugin table.
- One difference from an installed server is deliberate and not part of this fixture: JE's "Refresh CDN Assets" and "Send
  Anonymous Usage Report" tasks (like Jellyfin's "Update Plugins") have no triggers in the sandbox, so the sandbox
  never downloads JE's CDN assets or reports usage. `dev-server.ps1` writes those empty trigger lists before every
  start and `Initialize-Sandbox.ps1` asserts them (`scripts/README.md`, safety rules). JE features that show a CDN
  asset (flags, icons, theme sheets) go without it in the sandbox.
- The fixture keeps JE's outbound features off, which the same safety rule relies on: `AnalyticsEnabled` and
  `JellyseerrEnabled` are pinned false (and read back), and no Seerr, MDBList, Sonarr or Radarr URL or API key is set,
  so JE's other tasks that call out skip. `JeSandboxFixtureTests` fails if the XML gains any `*ApiKey`, `*Url(s)`,
  `*UrlMappings` or `*Instances` setting.
- Synthetic JE `reviews.json`, `bookmark.json` and `shortcuts.json` data lives next door in `kit/testdata/je-data`,
  with made-up ids, so `JeSandboxFixtureTests`' no-real-ids check of this folder stays strict; the kit's reader and
  parser tests use it, and the importers' sandbox checks (Bookmarks and User Ratings) copy it into a sandbox's JE data
  folder.
