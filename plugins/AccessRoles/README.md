# Access Roles

Access Roles is for anyone who shares a Jellyfin server with family, kids or guests. You make a few roles, such as
Kid or Guest, and give them to your users. A role can lock parts of a user's own settings, keep them away from the
Dashboard and from other users, and decide which libraries they can open. The server enforces it all in every app, not
just the web client, so you no longer have to edit each user by hand.

## Features

- Start from a Kid, Guest or Standard preset, then lock the settings you want left alone: profile, home screen,
  playback, subtitles, display, Quick Connect, the Jellyfin Enhanced panel and more.
- Pick which libraries each role can open. Give a single user extra libraries, hide some, or pick their own list.
- Library access is kept up to date for you when libraries are added, renamed or deleted.
- See who can open each library and why, and where each of a user's libraries and locks comes from.
- Spot library access that was changed outside Access Roles, and put it back or keep it.
- Keep non-admins away from the Dashboard's data, the server restart and other users' details.
- Search, people and suggestions only show items from libraries a user can open.
- New users get your default roles, including users made by sign-in plugins such as LDAP or SSO.
- Turn the library access your users already have into roles in one step, without changing anyone's access.
- See a preview of every change before it is saved, and look back in an audit log you can filter and download.
- Export, import and restore your setup, from a daily snapshot or onto another server.
- Locked settings simply disappear from the menus, so users aren't left wondering why a page won't save. A user with
  no libraries yet sees a note to ask you.

## Installing

Access Roles needs Jellyfin 12.4 or later.

1. In the Dashboard, go to **Plugins -> Repositories** and add
   `https://raw.githubusercontent.com/spooneyryan/jellyfin-plugins/main/manifest.json`.
2. Go to **Plugins -> Catalog**, find **Access Roles** and install it.
3. Restart the server, then reload the web client with **Ctrl+F5** in each browser.

Right after installing nobody is managed, so nothing changes for your users until you assign roles.

## Using it

Open **Dashboard -> Plugins -> Access Roles**. It is also in the Dashboard's menu.

- **Start from what you have.** On the *Overview* tab, press *Preview the import*, check that nothing changes for
  anyone, then *Apply the import*. You get one role for each set of libraries your users can open now.
- **Make or edit roles** on the *Roles* tab. Start from the Kid, Guest or Standard preset or a blank role, tick the
  settings to lock and pick the libraries. Set the default roles for new users at the top of the same tab.
- **Assign roles** on the *Users* tab. Tick some users and press *Assign roles…* or *Remove roles…*, or open one
  user to override a single lock, add or remove a library, or leave them out of Access Roles altogether.
- **Put back changes made elsewhere.** If a managed user's library access is changed on Jellyfin's own user page or by
  another tool, the *Users* tab marks them *Changed outside Access Roles*. Open the user to revert them, or keep their
  access as their own.
- **Find out why someone sees a library.** A user's panel shows where each library and lock comes from, and the
  *Libraries* tab shows who can open each library. If a library is deleted, the same tab lets you remove it from your
  roles, or grant it again once it is back.
- **Look back** on the *Audit* tab. Filter by user, action and date, and download the log.
- **Back up or move** on the *Data* tab. Export your setup, check an export before you import it, then merge it or
  replace what you have. You can also restore a snapshot. To move to another server, match users by their name.
- **Daily upkeep.** The scheduled task *Access Roles: Maintenance* runs each night. It takes the daily snapshot and
  trims the audit log.
- **Try it out.** Sign in as one of your users in each app they use and check that they see what you expect.

## Settings

Everything is on the *Settings* tab, and you see what a change means before you save it.

- **Enforce the roles**: untick it to pause everything for a while.
- **Configuration reads**: if a client app shows errors, add the setting it needs to the allow list, or pick *Redact*.
- **Only administrators can restart the server** and **Trim the public user list**: both on by default.
- **Library leak hardening**: keeps items from other libraries out of search, people and suggestions.
- **Write library roles to users' library access**: untick it to keep library roles stored and previewed, but not
  written.
- **When a managed user's library access is changed outside Access Roles**: put it back (the default), or keep it and
  report it.
- **When a navigation is blocked**: tell the user in a short message, or say nothing.
- **Show a banner on the stock user pages of managed users**: on by default.
- **Snapshots to keep** and **how long to keep the audit log**: a week of snapshots and a year of log by default.

## Questions and notes

- **Administrators are never restricted** by a lock, so you can always sign in as an admin and fix things. Access Roles
  also won't leave the server without an admin who can see every library unless you confirm it.
- **Library ticks on Jellyfin's own user page** are replaced by the roles when you save a managed user. Change their
  roles instead, or leave that user out of Access Roles. A banner on the user page reminds you.
- **Some settings live on the device.** *Display (this device)* and *Controls* are kept in the browser or app, so those
  two are hidden in the web client but can't be enforced.
- **Locked out?** Sign in as an admin and leave the user out or override the lock. If the plugin itself misbehaves, stop
  the server, create an empty file named `SAFE-MODE` in `data/accessroles/` inside Jellyfin's data folder, and start it
  again. Nothing is enforced until you delete the file and restart.
- **Removing the plugin.** Don't use *Disable* on the Plugins page. While Access Roles is still enabled, press
  *Release all locks* on the *Overview* tab (tick the box to give users back the library access they had before), then
  uninstall and restart.
- **Your data stays on your server.** Roles, the audit log and snapshots are kept in `data/accessroles/` in Jellyfin's
  data folder, and Access Roles talks to nothing but your own server. On the *Data* tab you can export, import and
  restore them.

## License

GPL-3.0-only.
