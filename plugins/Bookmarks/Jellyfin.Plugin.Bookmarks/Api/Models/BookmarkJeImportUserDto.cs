using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Bookmarks.Api.Models;

/// <summary>
/// One Jellyfin Enhanced user's row of the import report. The counts add up:
/// <c>Found = Import + Updated + Duplicates + AlreadyImported + Skipped</c>, and
/// <c>Import = MatchedById + MatchedByProviderId + Ambiguous + Detached</c>.
/// </summary>
public sealed class BookmarkJeImportUserDto
{
    /// <summary>
    /// Gets or sets the user id Jellyfin Enhanced's folder is named after (N format).
    /// </summary>
    public string JeUserId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Jellyfin user, or <c>null</c> when this server has no user with that id (every entry is skipped).
    /// </summary>
    public Guid? UserId { get; set; }

    /// <summary>
    /// Gets or sets the user's name, or <c>null</c> when unknown.
    /// </summary>
    public string? UserName { get; set; }

    /// <summary>
    /// Gets or sets how many entries the user's file holds, malformed ones included; 0 when it couldn't be read.
    /// </summary>
    public int Found { get; set; }

    /// <summary>
    /// Gets or sets how many bookmarks are added (or would be, in a dry run), attached or not.
    /// </summary>
    public int Import { get; set; }

    /// <summary>
    /// Gets or sets how many of <see cref="Import"/> are on the video JE's item id names.
    /// </summary>
    public int MatchedById { get; set; }

    /// <summary>
    /// Gets or sets how many of <see cref="Import"/> are on the one video with JE's TMDb or TVDb id.
    /// </summary>
    public int MatchedByProviderId { get; set; }

    /// <summary>
    /// Gets or sets how many of <see cref="Import"/> are on the first of several videos with JE's TMDb id.
    /// </summary>
    public int Ambiguous { get; set; }

    /// <summary>
    /// Gets or sets how many of <see cref="Import"/> match no video and are kept as "Not in library" (detached).
    /// </summary>
    public int Detached { get; set; }

    /// <summary>
    /// Gets or sets how many bookmarks imported before are rewritten from a JE entry changed since (not counted in
    /// <see cref="Import"/>).
    /// </summary>
    public int Updated { get; set; }

    /// <summary>
    /// Gets or sets how many entries are left out as duplicates.
    /// </summary>
    public int Duplicates { get; set; }

    /// <summary>
    /// Gets or sets how many entries an earlier import covered already.
    /// </summary>
    public int AlreadyImported { get; set; }

    /// <summary>
    /// Gets or sets how many entries are skipped: malformed, JE's cross-version copies, or a user this server doesn't have.
    /// </summary>
    public int Skipped { get; set; }

    /// <summary>
    /// Gets or sets how many of the imported or updated bookmarks were past the video's end and moved to a second before
    /// it.
    /// </summary>
    public int Clamped { get; set; }

    /// <summary>
    /// Gets or sets what kept this user's file from being imported, if anything: it couldn't be read (try again), or it is
    /// in a format this version doesn't know; and a note when the user isn't on this server. Nothing is written for a file
    /// that couldn't be read or isn't understood.
    /// </summary>
    public IReadOnlyList<string> Errors { get; set; } = [];
}
