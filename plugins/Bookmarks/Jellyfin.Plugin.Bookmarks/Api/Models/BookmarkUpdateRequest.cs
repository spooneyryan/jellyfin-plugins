using System;

namespace Jellyfin.Plugin.Bookmarks.Api.Models;

/// <summary>
/// Request body for editing a bookmark. All editable fields are replaced: a name or note left out (or <c>null</c>) is
/// cleared ("replace semantics"); a client that keeps a name sends it again. The one exception is Bookmarks 1.0's own
/// body, which has neither <see cref="Name"/> nor <see cref="IfUpdatedAt"/>: it keeps the name, since 1.0 knows no names
/// and would otherwise clear the name the 1→2 upgrade made from its note.
/// </summary>
/// <remarks>
/// <c>UpdateBookmarkRequest</c> in Bookmarks 1.0, renamed in 1.1.0 for the schema prefix.
/// Its 1.0 JSON is unchanged; only its OpenAPI schema id changed. <see cref="Name"/> and <see cref="IfUpdatedAt"/> are new
/// in 1.1.
/// </remarks>
public sealed class BookmarkUpdateRequest
{
    private string? _name;

    /// <summary>
    /// Gets or sets the bookmarked position (or range start) in ticks.
    /// </summary>
    public long StartTicks { get; set; }

    /// <summary>
    /// Gets or sets the range end in ticks, or <c>null</c> to make the bookmark a single position.
    /// </summary>
    public long? EndTicks { get; set; }

    /// <summary>
    /// Gets or sets the name, or <c>null</c> to clear it (new in 1.1). The rules of
    /// <see cref="BookmarkCreateRequest.Name"/> apply. Left out of a body that has no <see cref="IfUpdatedAt"/> either
    /// (Bookmarks 1.0's body), the bookmark keeps its name; left out of any other body, it is cleared.
    /// </summary>
    public string? Name
    {
        get => _name;
        set
        {
            _name = value;
            NameSent = true;
        }
    }

    /// <summary>
    /// Gets or sets the note, or <c>null</c> to clear it.
    /// </summary>
    public string? Note { get; set; }

    /// <summary>
    /// Gets or sets the bookmark's <see cref="BookmarkDto.UpdatedAt"/> as the client last read it (new in 1.1).
    /// When it is set and the bookmark was changed since, nothing is written and the answer is 409; without it the
    /// change is made whatever happened meanwhile, as in 1.0.
    /// </summary>
    public DateTime? IfUpdatedAt { get; set; }

    /// <summary>
    /// Gets a value indicating whether the body had a <see cref="Name"/> property, <c>null</c> included: JSON sets a
    /// property only when the body has it. Not part of the JSON or the OpenAPI schema.
    /// </summary>
    internal bool NameSent { get; private set; }
}
