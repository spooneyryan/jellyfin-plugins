using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// A look-up of a candidate clip file name failed for another reason than the name being free: the clip folder answered
/// with an I/O error other than "not found" (a drive that isn't ready, a share that doesn't answer) or refused access.
/// <see cref="ClipFileOps.NameTaken"/>'s rule throws it on the first such answer, so the choice of a name stops there
/// instead of asking about up to a thousand numbered names, each as slow as the folder, on a request's thread or under
/// <see cref="ClipFileOps.NameGate"/>; the planner and a rename turn it into "the clip folder can't be reached".
/// </summary>
[SuppressMessage(
    "Design",
    "CA1064:Exceptions should be public",
    Justification = "Thrown and caught inside the plugin only; public types are the plugin's public surface.")]
internal sealed class ClipFolderUnreachableException : IOException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ClipFolderUnreachableException"/> class.
    /// </summary>
    public ClipFolderUnreachableException()
        : base(ClipFileOps.FolderUnreachableMessage)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ClipFolderUnreachableException"/> class.
    /// </summary>
    /// <param name="message">The error message.</param>
    public ClipFolderUnreachableException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ClipFolderUnreachableException"/> class.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The file system's answer.</param>
    public ClipFolderUnreachableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
