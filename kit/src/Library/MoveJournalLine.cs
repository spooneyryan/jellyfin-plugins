using System;

namespace Jellyfin.Plugin.Kit.Library;

/// <summary>
/// One Committed line of Library Maintenance's move journal (format v1): the item <see cref="OldId"/>
/// became <see cref="NewId"/> when its files were moved. <see cref="NewId"/> is the id Library Maintenance predicted; a
/// later line with <see cref="Correction"/> leads from a wrong prediction to the actual id.
/// </summary>
/// <param name="Seq">The line's number, strictly increasing within the journal.</param>
/// <param name="Utc">When the move was committed.</param>
/// <param name="OldId">The item's id before the move.</param>
/// <param name="NewId">The item's id after it.</param>
/// <param name="Kind">The item's kind (<c>Movie</c>, <c>Episode</c>...), if given.</param>
/// <param name="OldPath">The path before the move, if given.</param>
/// <param name="NewPath">The path after it, if given.</param>
/// <param name="JobId">Library Maintenance's job, if given (additive field).</param>
/// <param name="Role">Primary, Version, Part or Extra, if given (additive field).</param>
/// <param name="Correction">Whether the line corrects a predicted id (additive field).</param>
internal sealed record MoveJournalLine(
    long Seq,
    DateTime Utc,
    Guid OldId,
    Guid NewId,
    string? Kind,
    string? OldPath,
    string? NewPath,
    Guid? JobId,
    string? Role,
    bool Correction);
