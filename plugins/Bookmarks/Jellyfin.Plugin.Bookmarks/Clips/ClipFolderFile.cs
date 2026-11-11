using System;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// A file <see cref="IClipFileSystem.ListClipFolder"/> found in a clip folder.
/// </summary>
/// <param name="Path">The file's full path.</param>
/// <param name="LastWriteTimeUtc">When it was last written.</param>
internal sealed record ClipFolderFile(string Path, DateTime LastWriteTimeUtc);
