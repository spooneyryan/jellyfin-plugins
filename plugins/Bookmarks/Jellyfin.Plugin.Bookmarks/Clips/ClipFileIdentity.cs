using System;
using System.Linq;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// What tells one clip file from another at the same path (since 1.2.0): the file's id on its volume (NTFS's
/// and ReFS's file id with the volume's serial number, which a rename within a folder keeps), its size and its last write
/// time. The rename journal records it before the move, so the recovery after a crash takes a file for the renamed clip only
/// when it is that file (<see cref="ClipRenameJournal"/>).
/// </summary>
/// <remarks>
/// <para>No creation time: NTFS's tunnelling gives a file renamed or created under a name freed less than 15
/// seconds earlier the freed file's creation time, so the renamed clip could read as another file, and another file as it.
/// The file id isn't tunnelled.</para>
/// <para>Where there is no id (a file system or share that answers none, a server that isn't Windows) the size and the last
/// write time alone tell the files apart (<see cref="IsSameFile"/>). An id is compared only when both sides have one: FAT and
/// exFAT number a file by its folder entry, which a rename may move, so a different id after the move doesn't by itself say
/// "another file" to the callers that prefer the renamed clip (<c>ClipFileOps</c>' recovery, with a Warning).</para>
/// </remarks>
/// <param name="Length">The size in bytes.</param>
/// <param name="WrittenUtc">When it was last written (UTC).</param>
internal sealed record ClipFileIdentity(long Length, DateTime WrittenUtc)
{
    /// <summary>
    /// Gets the serial number of the file's volume, or 0 when it isn't known.
    /// </summary>
    public ulong Volume { get; init; }

    /// <summary>
    /// Gets the file's id on its volume as 32 hexadecimal digits (<c>FILE_ID_128</c>; a 64-bit id in its low half), or
    /// <c>null</c> when it isn't known.
    /// </summary>
    public string? FileId { get; init; }

    /// <summary>
    /// Gets a value indicating whether the file's id is known: a volume and an id that aren't zero.
    /// </summary>
    public bool HasFileId => Volume != 0 && FileId is { Length: > 0 } id && id.Any(c => c != '0');

    /// <summary>
    /// Whether two identities are of the same file: the same size and last write time, and the same volume and id when both
    /// know theirs.
    /// </summary>
    /// <param name="recorded">The identity recorded (the journal's).</param>
    /// <param name="now">The identity read now.</param>
    /// <returns>Whether it is the same file; <c>false</c> when either is unknown.</returns>
    public static bool IsSameFile(ClipFileIdentity? recorded, ClipFileIdentity? now)
    {
        if (recorded is null || now is null || recorded.Length != now.Length || recorded.WrittenUtc.Ticks != now.WrittenUtc.Ticks)
        {
            return false;
        }

        return !recorded.HasFileId || !now.HasFileId
            || (recorded.Volume == now.Volume && string.Equals(recorded.FileId, now.FileId, StringComparison.OrdinalIgnoreCase));
    }
}
