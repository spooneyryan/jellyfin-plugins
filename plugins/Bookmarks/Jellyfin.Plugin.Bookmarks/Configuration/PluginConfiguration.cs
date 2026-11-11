using System;
using Jellyfin.Plugin.Bookmarks.Model;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Bookmarks.Configuration;

/// <summary>
/// Plugin settings, edited by administrators on the plugin's settings page.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    private int _missingClipRetentionDays = 30;

    /// <summary>
    /// Gets or sets a value indicating whether the Bookmarks controls are added to the web client. When off, the REST
    /// API still works. Read on every page load, so it applies without a restart.
    /// </summary>
    public bool EnableWebUi { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the plugin's user pages are registered with the Plugin Pages plugin at
    /// startup, in the <c>Auto</c> and <c>PluginPagesOnly</c> page host modes. Changes apply after a restart.
    /// The user pages are My Bookmarks (<see cref="EnableBookmarksPage"/>, since 1.1.0), My Clips
    /// (<see cref="EnableClipsPage"/>, since 1.2.0) and the developer demo's (<see cref="EnablePageDemo"/>).
    /// </summary>
    public bool RegisterPluginPages { get; set; } = true;

    /// <summary>
    /// Gets or sets where the plugin's user pages live: <c>FallbackOnly</c> (the default: their own
    /// routes, linked from the "Library tools" section of the drawer), <c>Auto</c> or <c>PluginPagesOnly</c> (the Plugin
    /// Pages plugin's section when it lists them). Read at startup for the Plugin Pages registration and on every page
    /// load for the links.
    /// </summary>
    public string PageHostMode { get; set; } = "FallbackOnly";

    /// <summary>
    /// Gets or sets a value indicating whether the plugin's pages are linked from the "Library tools" section of the
    /// drawer. Read on every page load.
    /// </summary>
    public bool ShowFallbackNav { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the server records the reports browsers send when a jellyfin-web element
    /// Bookmarks relies on is missing: one warning in the server log per element and web version,
    /// and the list on the settings page's Diagnostics section. Browsers warn in their console either way.
    /// </summary>
    public bool ReportWebAnchorProblems { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the developer demo of the kit's page hosting is on: two demo user pages,
    /// "Demo page A" and "Demo page B", that sandbox checks use. Off by default and not on the settings page; the head
    /// fragment and the pages exist only while it is on. Never turn it on on a server in use.
    /// </summary>
    public bool EnablePageDemo { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether "Demo page B" is on while <see cref="EnablePageDemo"/> is: turning it off
    /// shows the "turned off" note of a known page.
    /// </summary>
    public bool EnableDemoPageB { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the developer demo of the kit's item identity is on: the admin endpoints
    /// under <c>/Bookmarks/Admin/IdentityDemo</c> that take an item's identity snapshot, resolve a snapshot against the
    /// library and the move journal, and read the journal, which the sandbox check <c>scripts/identity-probe.mjs</c> uses.
    /// Off by default and not on the settings page; the endpoints answer 403 while it is off. Never turn it on on a server
    /// in use.
    /// </summary>
    public bool EnableIdentityDemo { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the developer demo of the kit's users and data services is on: a small
    /// store of demo records owned by users (<c>data/bookmarks/userdata-demo.json</c>) that the kit's deleted-user
    /// policy, reconciler, <i>Remap by name</i>, export, import and snapshot restore work on, behind the admin endpoints
    /// under <c>/Bookmarks/Admin/UserDataDemo</c>, which the sandbox check <c>scripts/users-probe.mjs</c> uses. Off by
    /// default and not on the settings page; the endpoints answer 403 while it is off, and a deleted user's demo records are
    /// then left alone. Never turn it on on a server in use.
    /// </summary>
    public bool EnableUserDataDemo { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the developer demo of the kit's library-change monitor is on: a consumer
    /// that records the batches of library changes it receives (ids, kinds and counts, no names), including the
    /// full pass of every library scan, behind the admin endpoints under <c>/Bookmarks/Admin/LibraryChangeDemo</c>,
    /// which the sandbox check <c>scripts/library-changes-probe.mjs</c> uses. Off by default and not on the settings page;
    /// while it is off the consumer takes no change and the endpoints answer 403. Never turn it on on a server in use.
    /// </summary>
    public bool EnableLibraryChangeDemo { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the developer demo of the kit's record lifecycle and library reads is on:
    /// a small store of item-linked demo records (<c>data/bookmarks/library-demo.json</c>, archive
    /// <c>archive/library-demo-archived.jsonl</c>) that the kit detaches (removed items, container ids, the verify sweep),
    /// relinks, archives after <see cref="DetachedRetentionDays"/>, reattaches from the archive and purges, plus
    /// read-only checks of <c>FreshUserData</c>, <c>LibraryAccess</c> and <c>VersionSets</c>, behind the admin endpoints
    /// under <c>/Bookmarks/Admin/LibraryDemo</c>, which the sandbox check <c>scripts/library-demo-probe.mjs</c> uses. Off by
    /// default and not on the settings page; while it is off the demo takes no library change, the kit leaves its records
    /// alone and the endpoints answer 403. Never turn it on on a server in use.
    /// </summary>
    public bool EnableLibraryDemo { get; set; }

    /// <summary>
    /// Gets or sets the days a detached record stays in the live store before it moves to the archive: 0, the default,
    /// keeps detached records for good, because bookmarks of a missing video must survive (the shared kit's default is
    /// 90). Bookmarks and the developer library demo (<see cref="EnableLibraryDemo"/>) follow it; a bookmark with a clip
    /// file is never archived. On the settings page ("Bookmark data").
    /// </summary>
    public int DetachedRetentionDays { get; set; }

    /// <summary>
    /// Gets or sets how many daily snapshots of <c>bookmarks.json</c> the "Bookmarks: Maintenance" task keeps in
    /// <c>data/bookmarks/snapshots</c>: 7 by default, 0 takes none. On the settings page ("Bookmark data"), whose Data tab
    /// lists the snapshots and restores one.
    /// </summary>
    public int SnapshotsToKeep { get; set; } = 7;

    /// <summary>
    /// Gets or sets how many days a <b>Debug</b> build moves the clock of the kit's record lifecycle on: the verify sweep's
    /// detach times, the retention of detached records and the daily maintenance. Release builds ignore it. 0 by default;
    /// for sandbox checks only.
    /// </summary>
    public int DeveloperClockOffsetDays { get; set; }

    /// <summary>
    /// Gets or sets what happens to a deleted user's data: <c>KeepAsOrphan</c> (the default:
    /// records without a file are removed, records with one are kept, visible only to admins), <c>Delete</c> (everything is
    /// removed) or <c>Anonymize</c> (as <c>KeepAsOrphan</c>). Applied only when Jellyfin deletes a user, or when an admin
    /// applies it to users that are gone. Bookmarks use it (the developer user data demo,
    /// <see cref="EnableUserDataDemo"/>, too). On the settings page ("Bookmark data").
    /// </summary>
    public string DeletedUserPolicy { get; set; } = "KeepAsOrphan";

    /// <summary>
    /// Gets or sets a value indicating whether the My Bookmarks page is on (since 1.1.0): off,
    /// its route and its shell show "My Bookmarks is turned off by your server admin." (read per request, so without a
    /// restart), no link leads to it (the "Library tools" section, the details page's "See all", the player's list) and
    /// the capabilities say <c>Pages.Bookmarks</c> false. Registration with Plugin Pages applies after a restart. On the
    /// settings page ("Pages").
    /// </summary>
    public bool EnableBookmarksPage { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the My Clips page is on (since 1.2.0): every clip of the
    /// signed-in user's across all videos. Off, its route and its shell show "My Clips is turned off by your server admin."
    /// (read per request, so without a restart), no link leads to it (the "Library tools" section, the My Bookmarks page's
    /// header, a video's bookmarks section, the player's list, the clip-ready toast) and the capabilities say
    /// <c>Pages.Clips</c> false, so the delete dialogs keep the 1.1.0 default of deleting a bookmark's clip with it.
    /// Registration with Plugin Pages applies after a restart. On the settings page ("Pages").
    /// </summary>
    public bool EnableClipsPage { get; set; } = true;

    /// <summary>
    /// Gets or sets the key combination that bookmarks the current moment in the web player:
    /// <c>Shift+B</c> by default, because Jellyfin Enhanced takes <c>B</c>; empty turns the shortcut off. Each user may
    /// override it on their own device. The capabilities report it and the player follows it.
    /// On the settings page ("Player"), whose field checks what is typed against the keys jellyfin-web and
    /// Jellyfin Enhanced take (the kit's <c>ShortcutPolicy</c>, <c>GET /Bookmarks/Admin/Shortcuts/Check</c>) and saves the
    /// normalised combination.
    /// </summary>
    public string BookmarkNowShortcut { get; set; } = "Shift+B";

    /// <summary>
    /// Gets or sets a value indicating whether the web player shows Bookmarks' three buttons (bookmark now, the range
    /// scissors, the list). The capabilities report it and the player follows it. On the settings page ("Player").
    /// </summary>
    public bool ShowPlayerButtons { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the player asks for a name as soon as a bookmark is made, for users who made
    /// no choice of their own (the server's default): off by default, so playback goes on and the toast offers "Name it".
    /// The capabilities report the effective value. On the settings page ("Player").
    /// </summary>
    public bool AskForNameDefault { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the name dialog suggests the name of the chapter the bookmark is in. The
    /// capabilities report it and the dialog follows it. On the settings page ("Player").
    /// </summary>
    public bool SuggestChapterNames { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the settings page shows the banner "Jellyfin Enhanced's Bookmarks is on. …"
    /// while Jellyfin Enhanced's own bookmarks are switched on (a setting the plugins built on the shared kit have in
    /// common). Off,
    /// the banner is left out (an admin who keeps both on purpose); the Data tab's import and switch-off checklist stay.
    /// Read on every load of the settings page.
    /// </summary>
    public bool ShowJellyfinEnhancedBanner { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether users may extract clips from their bookmarks.
    /// </summary>
    public bool EnableClips { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether admins can see and manage every user's clips (since 1.2.0): on
    /// (the default), the My Clips page offers admins an "All users" scope, which lists every user's clips with their owners,
    /// names and file paths (never a bookmark's note), and lets them preview, download, cancel and delete them
    /// (the <c>/Bookmarks/Admin/Clips…</c> routes). Off, those routes answer 403 and admins see only their own
    /// clips; the per-user totals (<c>/Bookmarks/Admin/Clips/Summary</c>) and Diagnostics stay. Read on every request. On the
    /// settings page ("Clips").
    /// </summary>
    public bool AdminsCanManageAllClips { get; set; } = true;

    /// <summary>
    /// Gets or sets the folder clips are written to. Empty means <c>data/bookmarks/clips</c> in the server's data folder,
    /// outside every library: keep it unless clips should be browsable in Jellyfin, and then use a folder of its
    /// own as the only folder of a "Home Videos &amp; Photos" library on a drive with room (never inside a Movies or TV
    /// library, whose scans take clips for the films and episodes they come from, and never on a nearly full drive).
    /// Everyone who can open that library sees every user's clips there.
    /// </summary>
    public string ClipOutputPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether each user's clips go into a sub-folder named after the user.
    /// </summary>
    public bool OrganizeClipsByUser { get; set; } = true;

    /// <summary>
    /// Gets or sets the mode used when a clip request doesn't specify one.
    /// </summary>
    public BookmarkClipMode DefaultClipMode { get; set; } = BookmarkClipMode.Accurate;

    /// <summary>
    /// Gets or sets the longest clip a user may extract, in seconds. Zero means no limit.
    /// </summary>
    public int MaxClipDurationSeconds { get; set; } = 600;

    /// <summary>
    /// Gets or sets how many clips may be encoded at the same time. Changes apply after a server restart.
    /// </summary>
    public int MaxConcurrentClips { get; set; } = 1;

    /// <summary>
    /// Gets or sets a value indicating whether the files clip jobs left unfinished when the server stopped without finishing
    /// them (a crash, a killed process: <c>&lt;clip file&gt;.partial</c>) are deleted when the server starts, before
    /// interrupted clip jobs resume. Only files named as Bookmarks names a clip being written, in the recorded
    /// clip folders and the folders directly inside them, and last written before the server started are deleted; other
    /// programs' <c>.partial</c> files are never touched. Read at every start.
    /// </summary>
    public bool CleanUpPartialFilesOnStartup { get; set; } = true;

    /// <summary>
    /// Gets or sets how many days a clip whose file has gone (deleted by hand, or through Jellyfin) keeps its entry
    /// (since 1.2.0): 30 by default, 0 for good, at most 36500 (<c>MissingClipSweep.MaxRetentionDays</c>).
    /// The daily maintenance marks a clip file missing from its own clip folder (<c>ClipInfo.MissingSinceUtc</c>) and clears
    /// the mark when the file is back; after this many days a deleted bookmark's kept clip moves into the archive
    /// (<c>archive/bookmarks-archived.jsonl</c>, never deleted on a timer), and a bookmark's own clip entry is cleared while the
    /// bookmark stays. A clip folder is judged only while it is there with its marker file (<c>.jfbm-clip-root</c>, written
    /// the first time its clips are found there, none of them gone): one that isn't there (an unplugged drive), that is there
    /// without its marker and without all of its clips (a disk that isn't mounted, a folder in its place), or in which every
    /// clip is gone at once is left out, so nothing in it is marked or archived, until an admin confirms it on the Diagnostics
    /// tab; a clip being made is left alone. Read at every run. On the settings page ("Clips"). A
    /// value outside 0 to 36500 is stored as the nearest of the two, also when an older file or another client sets it, so
    /// the settings page's field (0 to 36500) never loads a value it couldn't save.
    /// </summary>
    public int MissingClipRetentionDays
    {
        get => _missingClipRetentionDays;
        set => _missingClipRetentionDays = Math.Clamp(value, 0, Clips.MissingClipSweep.MaxRetentionDays);
    }

    /// <summary>
    /// Gets or sets how much space, in MB, must stay free on the clip folder's drive (the free-space guard):
    /// a clip is refused when its estimated size and those of the clips waiting to be made would leave less, when it is
    /// asked for and again when it starts, and a clip being made is stopped when less than half of it is left. The clip
    /// folder's line on the settings page warns when less than this plus 1 GB is free (<c>LowFreeSpace</c>). 0 keeps no
    /// reserve: a clip is refused only when it wouldn't fit, and the watchdog never stops one. Read every time it is used.
    /// </summary>
    public int MinFreeSpaceMegabytes { get; set; } = 2048;

    /// <summary>
    /// Gets or sets the x264 preset used by accurate clips.
    /// </summary>
    public string VideoPreset { get; set; } = "veryfast";

    /// <summary>
    /// Gets or sets the x264 constant rate factor used by accurate clips (lower is higher quality).
    /// </summary>
    public int VideoCrf { get; set; } = 20;

    /// <summary>
    /// Gets or sets the maximum height of accurate clips in pixels. Zero keeps the source resolution.
    /// </summary>
    public int MaxVideoHeight { get; set; }

    /// <summary>
    /// Gets or sets the AAC bitrate of accurate clips, in kbit/s.
    /// </summary>
    public int AudioBitrateKbps { get; set; } = 192;

    /// <summary>
    /// Gets or sets the number of audio channels in accurate clips.
    /// </summary>
    public int AudioChannels { get; set; } = 2;
}
