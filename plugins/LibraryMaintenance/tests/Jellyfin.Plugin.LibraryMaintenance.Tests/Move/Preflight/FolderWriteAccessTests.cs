using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.LibraryMaintenance.Move.Preflight;
using Xunit;

namespace Jellyfin.Plugin.LibraryMaintenance.Tests.Move.Preflight;

/// <summary>
/// The plan-time write check that writes nothing: the folder's ACL for adding
/// files and for adding folders, each on its own, and the volume's read-only flag; and it changes nothing on disk.
/// </summary>
public sealed class FolderWriteAccessTests : IDisposable
{
    // LibraryMonitor's watcher filter (v10.11.5:Emby.Server.Implementations/IO/LibraryMonitor.cs:233-240).
    private const NotifyFilters LibraryMonitorFilter = NotifyFilters.CreationTime | NotifyFilters.DirectoryName | NotifyFilters.FileName
        | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.Attributes;

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void CanAdd_AnOpenFolder_ForFilesFoldersOrBoth()
    {
        var folder = Folder("Open");
        var access = new FolderWriteAccess(static _ => false);
        Assert.True(access.CanAdd(folder, files: true, folders: false));
        Assert.True(access.CanAdd(folder, files: false, folders: true));
        Assert.True(access.CanAdd(folder, files: true, folders: true));
        Assert.False(access.CanAdd(_temp.Combine("Missing"), files: true, folders: false));
    }

    [Fact]
    public void CanAdd_ReadsTheFoldersAcl_ForEachKindOnItsOwn()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("ACLs are tested on Windows.");
            return;
        }

        var access = new FolderWriteAccess(static _ => false);
        var noFiles = Folder("NoFiles");
        using (new DenyAccess(noFiles, FileSystemRights.CreateFiles))
        {
            Assert.False(access.CanAdd(noFiles, files: true, folders: false));
            Assert.True(access.CanAdd(noFiles, files: false, folders: true));
            Assert.False(access.CanAdd(noFiles, files: true, folders: true));
        }

        var noFolders = Folder("NoFolders");
        using (new DenyAccess(noFolders, FileSystemRights.CreateDirectories))
        {
            Assert.True(access.CanAdd(noFolders, files: true, folders: false));
            Assert.False(access.CanAdd(noFolders, files: false, folders: true));
        }

        Assert.True(access.CanAdd(noFiles, files: true, folders: true));
    }

    [Fact]
    public void CanAdd_OpensAJunctionItself_NeverItsTarget()
    {
        // Without FILE_FLAG_OPEN_REPARSE_POINT the handle would follow the link: a dangling
        // junction opens as itself; followed, it would be a folder that isn't there.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Junctions are a Windows feature.");
        var target = Folder("Gone");
        var link = _temp.Combine("Dangling");
        Move.Paths.Junction.Create(link, target);
        Directory.Delete(target);

        Assert.True(new FolderWriteAccess(static _ => false).CanAdd(link, files: true, folders: false));
    }

    [Fact]
    public void CanAdd_OnAReadOnlyVolume_IsRefused()
    {
        var folder = Folder("Disc");
        var asked = new List<string>();
        var access = new FolderWriteAccess(path =>
        {
            asked.Add(path);
            return true;
        });

        Assert.False(access.CanAdd(folder, files: true, folders: false));
        Assert.Equal([folder], asked);
    }

    [Fact]
    public void CanAdd_IsAskedOfTheRealVolume_WhichIsWritable()
    {
        // The default asks Windows (GetVolumeInformationW); the temp folder's volume isn't read-only.
        Assert.True(new FolderWriteAccess().CanAdd(Folder("Real"), files: true, folders: true));
    }

    [Fact]
    public async Task CanAdd_ChangesNothing_NoEntryNoWriteTimeNoWatcherEvent()
    {
        var folder = Folder("Season 02");
        File.WriteAllBytes(Path.Join(folder, "Ep 1.mkv"), [1]);
        var before = Directory.EnumerateFileSystemEntries(_temp.Path, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToList();
        var written = Directory.GetLastWriteTimeUtc(folder);
        var events = new List<string>();
        using (var watcher = new FileSystemWatcher(_temp.Path) { IncludeSubdirectories = true, NotifyFilter = LibraryMonitorFilter })
        {
            void Seen(object sender, FileSystemEventArgs e)
            {
                lock (events)
                {
                    events.Add(e.ChangeType + " " + e.FullPath);
                }
            }

            watcher.Created += Seen;
            watcher.Changed += Seen;
            watcher.Deleted += Seen;
            watcher.Renamed += Seen;
            watcher.EnableRaisingEvents = true;

            var access = new FolderWriteAccess();
            for (var i = 0; i < 5; i++)
            {
                Assert.True(access.CanAdd(folder, files: true, folders: true));
            }

            await Task.Delay(500, TestContext.Current.CancellationToken);
        }

        Assert.Equal(before, Directory.EnumerateFileSystemEntries(_temp.Path, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal));
        Assert.Equal(written, Directory.GetLastWriteTimeUtc(folder));
        lock (events)
        {
            Assert.Empty(events);
        }
    }

    private string Folder(string name)
    {
        var path = _temp.Combine(name);
        Directory.CreateDirectory(path);
        return path;
    }
}
