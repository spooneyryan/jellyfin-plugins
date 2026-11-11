using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Bookmarks.Model;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// What <see cref="ClipFileBatchDelete.RunAsync"/> did.
/// </summary>
/// <param name="Changed">The records changed (their clip file gone, or none), as they were before.</param>
/// <param name="Refused">The records left as they were, with what their file's delete answered: in use after every attempt,
/// refused by the guard, in a clip folder that can't be reached, or failed otherwise.</param>
/// <param name="InProgress">The records left as they were because their clip was being made (a job waiting or running),
/// when the caller leaves those alone; nothing of them was touched.</param>
internal sealed record ClipFileBatchOutcome(IReadOnlyDictionary<Guid, Bookmark> Changed, IReadOnlyDictionary<Guid, ClipFileResult> Refused, IReadOnlySet<Guid> InProgress)
{
    /// <summary>
    /// Gets the records among <see cref="Changed"/> whose clip file was kept, not deleted, because a record outside the batch
    /// still names it (Bookmarks 1.1.3's rule, <see cref="ClipFileOutcome.NamedByAnotherRecord"/>): they let go of it, and the
    /// admins' audit says so rather than "deleted".
    /// </summary>
    public IReadOnlySet<Guid> FilesKept { get; init; } = new HashSet<Guid>();
}
