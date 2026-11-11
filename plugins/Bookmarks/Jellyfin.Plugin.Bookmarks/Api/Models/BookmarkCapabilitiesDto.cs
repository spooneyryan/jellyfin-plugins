using System.Collections.Generic;
using Jellyfin.Plugin.Bookmarks.Model;

namespace Jellyfin.Plugin.Bookmarks.Api.Models;

/// <summary>
/// What the current user can do with bookmarks, so clients can show the right controls.
/// </summary>
public sealed class BookmarkCapabilitiesDto
{
    /// <summary>
    /// Gets or sets a value indicating whether clip extraction is enabled on the server.
    /// </summary>
    public bool ClipsEnabled { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the current user may create and download clips.
    /// </summary>
    public bool CanCreateClips { get; set; }

    /// <summary>
    /// Gets or sets the longest allowed clip in seconds; zero means no limit.
    /// </summary>
    public int MaxClipDurationSeconds { get; set; }

    /// <summary>
    /// Gets or sets the mode used when a clip request doesn't name one.
    /// </summary>
    public BookmarkClipMode DefaultClipMode { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the caller passes Jellyfin's <c>RequiresElevation</c> policy on this
    /// request, so clients can show admin extras. The server checks again on every admin request.
    /// </summary>
    public bool IsAdmin { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the bookmark data is read-only, because it was written by a newer plugin
    /// version or couldn't be upgraded. Every change then fails with 503; reading still works.
    /// </summary>
    public bool StoreReadOnly { get; set; }

    /// <summary>
    /// Gets or sets why the bookmark data is read-only, so the client can word its banner: a newer plugin
    /// version's data, or data that couldn't be upgraded. Left out while <see cref="StoreReadOnly"/> is false.
    /// </summary>
    public BookmarkStoreReadOnlyReason? StoreReadOnlyReason { get; set; }

    /// <summary>
    /// Gets or sets the longest name, in UTF-16 code units: 100. New in 1.1.
    /// </summary>
    public int MaxNameLength { get; set; }

    /// <summary>
    /// Gets or sets the longest note, in UTF-16 code units: 500. New in 1.1.
    /// </summary>
    public int MaxNoteLength { get; set; }

    /// <summary>
    /// Gets or sets the server's keyboard shortcuts (<c>BookmarkNow</c>, <c>Shift+B</c> by default). New in 1.1.
    /// </summary>
    public BookmarkShortcutsDto Shortcuts { get; set; } = new();

    /// <summary>
    /// Gets or sets the key combinations a user's own shortcut may not use: jellyfin-web's player keys and Jellyfin
    /// Enhanced's configured ones, as the shared kit's shortcut policy reads them. New in 1.1.
    /// </summary>
    public IReadOnlyList<string> ReservedShortcuts { get; set; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether the player shows Bookmarks' three buttons (<c>ShowPlayerButtons</c>). New in
    /// 1.1.
    /// </summary>
    public bool ShowPlayerButtons { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the player asks the caller for a name as soon as a bookmark is made: their
    /// own choice, else the server's default (<see cref="AskForNameIsDefault"/>). New in 1.1.
    /// </summary>
    public bool AskForName { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether <see cref="AskForName"/> is the server's default because the caller made no
    /// choice ("Use server default"). New in 1.1.
    /// </summary>
    public bool AskForNameIsDefault { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the server's default (the setting <c>AskForNameDefault</c>) asks for a
    /// name, whatever the caller chose, so a client can name it next to "Use the server's default" once the caller has a
    /// choice of their own. New in 1.1.
    /// </summary>
    public bool AskForNameDefault { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether playback pauses while the caller's name dialog is open (their own
    /// preference; off by default). New in 1.1.
    /// </summary>
    public bool PauseWhileNaming { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the name dialog suggests the chapter's name (<c>SuggestChapterNames</c>).
    /// New in 1.1.
    /// </summary>
    public bool SuggestChapterNames { get; set; }

    /// <summary>
    /// Gets or sets which of Bookmarks' user pages the server offers. New in 1.1.
    /// </summary>
    public BookmarkPagesDto Pages { get; set; } = new();

    /// <summary>
    /// Gets or sets a value indicating whether, for an admin, Jellyfin Enhanced has bookmarks on this server that no applied
    /// import covers yet: the My Bookmarks page's empty state then adds "Have bookmarks in Jellyfin Enhanced? Import them in
    /// the plugin settings." Always false for everyone else. New in 1.1.
    /// </summary>
    public bool JellyfinEnhancedImportPending { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the current user may download and preview clip files: Jellyfin's "Allow media
    /// downloading" permission, which the clip file route requires (<c>Policies.Download</c>), whether or not making clips
    /// is turned on (existing clips stay downloadable). The My Clips page shows Preview and Download only then. New in 1.2.
    /// </summary>
    public bool CanDownloadClips { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the caller may see and manage every user's clips: an
    /// admin on this request (<see cref="IsAdmin"/>) while the setting <c>AdminsCanManageAllClips</c> is on. The My Clips page
    /// offers its "All users" scope only then; the server checks both again on every admin clip request. New in 1.2.
    /// </summary>
    public bool CanManageAllClips { get; set; }
}
