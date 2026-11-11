using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Jellyfin.Plugin.Bookmarks.Clips;

namespace Jellyfin.Plugin.Bookmarks.Tests.TestSupport;

/// <summary>
/// The real file system, but a clip folder reads as unplugged the first time it is asked about and as there afterwards: a
/// drive plugged back in between a request's check before the store's lock and any later look. Deletes are
/// counted.
/// </summary>
internal sealed class FolderBackAfterFirstLook : IClipFileSystem
{
    private int _folderLooks;
    private int _deletes;

    /// <summary>Gets how many times a clip folder was asked about.</summary>
    public int FolderLooks => Volatile.Read(ref _folderLooks);

    /// <summary>Gets how many deletes were asked for.</summary>
    public int Deletes => Volatile.Read(ref _deletes);

    public bool FolderExists(string path) => Interlocked.Increment(ref _folderLooks) > 1 && PhysicalClipFileSystem.Instance.FolderExists(path);

    public FileAttributes? GetAttributes(string path) => PhysicalClipFileSystem.Instance.GetAttributes(path);

    public void DeleteFile(string path)
    {
        Interlocked.Increment(ref _deletes);
        PhysicalClipFileSystem.Instance.DeleteFile(path);
    }

    public void MoveFile(string source, string destination) => PhysicalClipFileSystem.Instance.MoveFile(source, destination);
    public void ReplaceFile(string source, string destination) => PhysicalClipFileSystem.Instance.ReplaceFile(source, destination);

    public IEnumerable<ClipFolderFile> ListClipFolder(string folder) => PhysicalClipFileSystem.Instance.ListClipFolder(folder);

    public DateTime? GetLastWriteTimeUtc(string path) => PhysicalClipFileSystem.Instance.GetLastWriteTimeUtc(path);
}
