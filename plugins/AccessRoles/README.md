# Access Roles

Access Roles gives your Jellyfin users **roles**. A role can lock sections of a user's own Settings menu (their profile,
home screen, playback settings and so on), keep them away from the Dashboard's data and from other users, and decide
which libraries they can open. The server enforces all of this for every client, not just the web client. It is for
home-server admins who share their server with family, kids or guests and want each person to see their own libraries
and leave their settings alone, without editing every user by hand.

## Features

- **Roles** with any mix of locks: Profile (password, picture, name), Display (account), Display (this device), Home
  screen, Playback, Subtitles, Controls, Quick Connect, Jellyfin Enhanced settings, *Block Dashboard data and server
  calls*, and *Hide other users*. New roles can start from the **Kid**, **Guest** or **Standard** preset.
- **Library grants** per role: not managed, all libraries, all except some, or only some. Per user you can add or remove
  libraries, or override any single lock.
- **Library access is written for you.** Users' library access (Jellyfin's "Enable access to all libraries" and the
  library ticks) follows their roles, and is kept that way when libraries are added, renamed or deleted.
- **Default roles** for new users, including users created by LDAP or SSO plugins.
- **Import current setup** turns the library access your users already have into roles, changing nobody's access.
- **Every change is previewed** before it is saved: who gets which lock, who gains or loses which library.
- **Server-wide hardening for every non-admin** (on by default): non-admins can't read the server's configuration
  (except what the video player needs), can't restart the server (shutting it down is already for administrators only in Jellyfin), the public user list on the sign-in
  screen shows only what that screen needs, and search, people, suggestions and similar answers stop showing items from
  libraries a user can't open.
- An **audit log** of every change, **export, import and snapshot restore** of the roles, and a daily maintenance task.
- For **members**: locked settings simply disappear from the menus in the web client, and a locked page sends them back
  to Home with "This page is managed by your server administrator."

## Requirements

- **Jellyfin 10.11.5 or later within 10.11.** Jellyfin 12 and later are not supported yet (on a newer major version the plugin
  stays inactive and its page says why).
- **Other plugins: none required.** Access Roles injects its own web script; it does not need File Transformation or
  any other plugin.
- **Jellyfin Enhanced is optional.** If it is installed, the *Jellyfin Enhanced settings* lock keeps locked users out of
  Jellyfin Enhanced's settings panel and its settings saves. Without it, that lock simply does nothing. Access Roles was
  checked against Jellyfin Enhanced 12.14; with another version, look at the *Diagnostics* tab's rule table for anything
  marked as not reviewed. Some of Jellyfin Enhanced's per-item features are deliberately **not** locked: a locked user can
  still hide and unhide items on Continue Watching and Next Up, manage their Spoiler Guard list, write reviews and keep
  bookmarks.
- **Web client layouts:** the web guard supports jellyfin-web's legacy (desktop) layout, the experimental layout, the TV
  layout and phone-sized screens. The admin page works with a keyboard, a TV remote's D-pad and on a phone.
- **Tested on**: Windows only, with Jellyfin 10.11.5. Linux and Docker servers have not been tested yet.

## Installation

**From the plugin repository**

1. Dashboard -> Plugins -> Repositories, add `https://raw.githubusercontent.com/spooneyryan/jellyfin-plugins/main/manifest.json`.
2. Dashboard -> Plugins -> Catalog, find **Access Roles** and install it.
3. Restart the server.

**Manually**

1. Download the zip from the GitHub Releases page: <https://github.com/spooneyryan/jellyfin-plugins/releases>.
2. Extract it into the `plugins/Access Roles_1.0.3.0` folder inside Jellyfin's data folder.
3. Restart the server.

**After installing**, reload the web client with **Ctrl+F5** in each browser, because Access Roles adds a script to the
web client.

Right after installing nobody is managed, so no role, lock or library change applies to anyone. Only the server-wide
hardening for non-admins is active (see Settings below if a client app has trouble).

## Getting started

Open **Dashboard -> Plugins -> Access Roles** (it is also in the Dashboard's menu). The page has the tabs Overview,
Roles, Users, Libraries, Audit, Settings, Data and Diagnostics.

1. **Import what you have.** On the *Overview*, under *Import current setup*, press *Preview the import*. It proposes one
   role per distinct library access among your non-admin users (named after the libraries, with no locks). The preview
   should say nothing changes for anyone. Then *Apply the import*. Administrators and users who see every library are
   left as they are, unless you tick *Also manage users who see every library*.
2. **Create or edit roles** on the *Roles* tab: *New role*, pick a preset or start blank, tick the sections to lock and
   choose the libraries.
3. **Assign roles** on the *Users* tab: tick users, then *Assign roles…*, or open a user's panel.
4. **Set default roles** for new users at the top of the *Roles* tab.
5. **Test with one user**: sign in as them in each app they use and check that their libraries and settings are what
   you expect.

## Using it

- **Change a user's access:** open their name on the *Users* tab. You can override single locks, set their own library
  mode with *Add* and *Remove* lists, mark them *Excluded* (Access Roles leaves them alone), or *Unmanage user…*. When a
  user leaves library management you choose: *Restore original access*, *Keep current access* or *Grant all libraries*.
- **See why someone sees a library:** the user's panel shows their effective access and where each library and lock
  comes from; the *Libraries* tab shows who opens each library and flags differences.
- **Editing users on the Dashboard:** for a managed user, the library ticks you set on Jellyfin's own *Library access*
  tab are replaced by the roles' result when you save (the audit log lists what was undone). Change their roles or
  overrides instead, or set them *Excluded*. A banner on the stock user pages reminds you.
- **Changes made outside Access Roles** (by another admin, script or plugin) are written back by default. Under
  *Settings*, *When a managed user's library access is changed outside Access Roles* can instead keep and report them.
- **Settings** tab: every option, previewed with *Review and save…* before saving. The server-wide switches are here:
  *Library leak hardening*, *Configuration reads* (if a client app shows errors, add the key it needs to the allow
  list, or choose *Redact*), *Only administrators can restart the server* and *Trim the public user list*.
- **Audit** tab: filter and download the log.

## Important notes and limitations

- **Administrators and API keys are never restricted** by any lock or server-wide rule, and the admin page needs only an
  administrator, so an admin can always fix things. Library roles *do* apply to an administrator if you assign one, but
  Access Roles refuses to leave the server without at least one enabled administrator who sees every library, unless
  you explicitly confirm it.
- **Hiding in the web client is cosmetic; the server enforces.** Even if the web script fails or is switched off, the
  server still refuses or undoes locked changes from any client. A locked settings change made in an app is silently
  undone, so the app may show it until it reloads its settings. *Display (this device)* and *Controls* are stored in the
  browser or app, so those two are only hidden, not enforced, and settings an app keeps on the device can't be locked.
- **Don't use Disable on the Plugins page.** On Jellyfin 10.11.5, *Disable* takes effect only at the next restart (the
  locks keep working until then), Jellyfin then deletes the disabled plugin's folder, and nothing is put back. To stop
  enforcing for a while, untick *Enforce the roles* on the *Settings* tab. To remove the plugin, see Uninstalling.
- **Turning enforcement off and restarting** makes the plugin inactive: only its admin page loads (with a *Turn
  enforcement back on* button). While the server runs, the switch only pauses enforcement.
- **Missing or unmounted library drives:** Access Roles keeps nothing on your media drives, and it decides whether a
  library exists from Jellyfin's own library list, not from the media folders, so an unmounted drive does not remove
  anyone's access to that library.
- **Other user managers:** the Overview warns if it sees an SSO, LDAP, OpenID or similar user-management plugin. Access
  Roles corrects a managed user's library access at every sign-in, including sign-ins through such plugins.
- **A sign-in may be refused briefly** (an error asking to try again) when Access Roles can't correct a non-admin's
  library access at that moment, or while the library sync is held after a restore. Admin sign-ins are never refused.
- **The optional native mirror** (*Also turn native preference access off when … are all locked*, off by default) also
  turns off Jellyfin's own preference access for fully locked users. That flag has no checkbox on the Dashboard, stays
  if the plugin disappears, and, as the setting's note says, also blocks those users' own watched states and
  favourites. Leave it off unless you need it.

### Lockout and recovery

1. **A user is locked out of something.** Sign in as an administrator (never restricted). On the *Users* tab, set the
   user *Excluded* or override the lock, or untick *Enforce the roles* on the *Settings* tab to pause everything at once.
2. **The emergency stop**, when the plugin itself misbehaves. Stop the server. Create an empty file named `SAFE-MODE` in
   `<Jellyfin data folder>/data/accessroles/`, or set `<EnforcementEnabled>false</EnforcementEnabled>` in
   `<Jellyfin data folder>/plugins/configurations/Jellyfin.Plugin.AccessRoles.xml`. Start the server: the plugin is
   inactive, nothing is enforced and nothing writes users' policies; its page says which switch is in effect. Library
   access already written stays as it is. To undo, delete the file (or use *Turn enforcement back on*) and restart.
3. **Degraded mode.** If the data file can't be read or trusted at startup, a banner says so and the users it lists as
   managed get every server-enforced section locked (every non-admin, if nothing at all can be read). Put a readable
   copy back with the server stopped (from `snapshots/` or the `.bak` file next to it), or confirm on the Overview with
   *Stop locking them* once you have checked.
4. **Before removing the plugin**, use *Release all locks* (Overview, *Before disabling or deleting Access Roles*) while
   it is still enabled. Tick *Also restore each user's original library access* to give everyone back what they had
   before Access Roles managed them. Released users become *Excluded* (their roles are kept).
5. **The plugin folder was deleted while the native mirror was on:** see the recovery right below.

**Recovery after the plugin folder was deleted with the mirror on.** Nothing of the plugin runs any more, so put the flag
back with an API key (Dashboard -> API Keys). The affected users are the ids under `PreferenceMirror` in
`data/accessroles/accessroles.json`, which stays in Jellyfin's data folder. For each of them, the line below reads the
user's policy, sets `EnableUserPreferenceAccess` to true and posts the whole policy back as UTF-8. Run it in Windows
PowerShell 5.1 or PowerShell 7 after adjusting the three values: the server's address, the API key and the data file
(the path shown is the Windows default).

```powershell
$server = 'http://localhost:8096'; $key = '<API key>'; $data = 'C:\ProgramData\Jellyfin\Server\data\accessroles\accessroles.json'
$h = @{ Authorization = "MediaBrowser Token=`"$key`"" }; foreach ($id in (Get-Content $data -Raw -Encoding UTF8 | ConvertFrom-Json).PreferenceMirror.PSObject.Properties.Name) { $p = (Invoke-RestMethod "$server/Users/$id" -Headers $h).Policy; $p.EnableUserPreferenceAccess = $true; Invoke-RestMethod -Method Post "$server/Users/$id/Policy" -Headers $h -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes(($p | ConvertTo-Json -Depth 10))) }
```

Then delete the API key.

## Your data and privacy

- Everything is stored in Jellyfin's data folder (e.g. `%ProgramData%\Jellyfin\Server` on Windows, `/var/lib/jellyfin`
  on Linux, `/config` in Docker), under `data/accessroles/`: `accessroles.json` (roles, assignments, each managed user's
  original library access, user names) with a `.bak` copy, `snapshots/` (a daily snapshot, 7 kept by default, plus one
  before each import or restore), and `audit/` (one log file per month, kept 12 months by default). The settings are in
  `plugins/configurations/Jellyfin.Plugin.AccessRoles.xml`.
- The daily task *Access Roles: Maintenance* (03:50) takes the snapshot and trims the audit log.
- *Data* tab: **Export** the roles and assignments to a file, **Import** one (Merge or Replace, users matched by id or
  name, libraries by id, folder or name, after a dry run and a safety snapshot), or **Restore** a snapshot. After a
  restore, library access is not written until you preview and confirm the sync on the Overview. An export holds user
  and library names: keep it safe.
- Only administrators can see the admin page, the audit log and other users' roles. A user only learns their own locks.
- Access Roles sends nothing to any external service; it talks only to your own Jellyfin server.

## Uninstalling

1. While the plugin is still enabled, use *Release all locks* on the Overview (tick *Also restore each user's original
   library access* if you want users' old library access back). Without it, users' library access stays as last
   written.
2. Dashboard -> Plugins -> Access Roles -> *Uninstall*, then restart. Uninstalling an enabled plugin turns back on the
   native preference access (`EnableUserPreferenceAccess`) the optional mirror had turned off; it does not change library
   access.
3. The data folder `data/accessroles/` and the settings file stay behind, so reinstalling finds them. Delete them
   yourself, with the server stopped, if you want everything gone.

## More detail

The full technical documentation is in [DEVELOPMENT.md](DEVELOPMENT.md) in this folder. The changelog is the
`changelog` in [build.yaml](build.yaml).
