using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Jellyfin.Plugin.Bookmarks.Clips;
using Jellyfin.Plugin.Bookmarks.Services;
using Jellyfin.Plugin.Kit.TestSupport.Api;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Clips;

/// <summary>
/// Every delete, rename, move and serve goes through <c>ClipFileGuard</c>: in the compiled code of Bookmarks (async state
/// machines and lambdas included), only <see cref="PhysicalClipFileSystem"/>, behind <see cref="ClipFileOps"/>, deletes,
/// moves or replaces a file or folder. So anything that deletes or moves files (the deleted-user <c>Delete</c> policy, the
/// rename, bulk deletes, the untracked-file delete, and whatever comes next) fails here unless it uses
/// <see cref="ClipFileOps"/>. The kit's source compiled into the assembly (its store's own files) is not Bookmarks' and is
/// left out.
/// </summary>
public sealed class FileOperationsCoverageTests
{
    private static readonly Assembly PluginAssembly = typeof(Plugin).Assembly;

    private static readonly Type[] FileSystemTypes = [typeof(File), typeof(FileInfo), typeof(Directory), typeof(DirectoryInfo), typeof(FileSystemInfo)];

    private static readonly string[] Changes = ["Delete", "Move", "MoveTo", "Replace"];

    [Fact]
    public void OnlyTheClipFileSystem_DeletesOrMovesFiles()
    {
        var callers = PluginTypes()
            .Where(t => Members(t).OfType<MethodInfo>().Any(IsDeleteOrMove))
            .Select(t => t.FullName)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal([typeof(PhysicalClipFileSystem).FullName], callers);
    }

    [Fact]
    public void OnlyThesePartsOfBookmarks_CallTheGuardsOwnCheck_EverythingElseGoesThroughClipFileOps()
    {
        // Since 1.2.0 (clip rename, and the admins' delete and bulk delete): ClipFileGuard.Check alone doesn't hold the
        // paths of a clip file rename that isn't finished, ClipFileOps.Check does; so anything new that deletes, forgets, moves
        // or serves a clip file fails here until it goes through ClipFileOps. The list below: ClipFileOps itself (its Check,
        // the recovery's judging on the document being written, the move back, CountClips), the My Clips list's cache (which
        // only shows), the relink of a restore (on the document being written, never touching a file), and the import (which
        // keeps or drops an export's path).
        var callers = PluginTypes()
            .Where(t => t != typeof(ClipFileGuard) && Members(t).OfType<MethodInfo>().Any(m => m.DeclaringType == typeof(ClipFileGuard) && m.Name == nameof(ClipFileGuard.Check)))
            .Select(t => t.FullName)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            [
                typeof(ClipFileOps).FullName,
                typeof(ClipRenameRelink).FullName,
                typeof(ClipStateCache).FullName,
                typeof(Jellyfin.Plugin.Bookmarks.Storage.BookmarkImportAdapter).FullName
            ],
            callers);
    }

    [Fact]
    public void TheCheck_SeesADeleteInAnAsyncMethod()
    {
        // The check itself: an async method's body is its state machine, which it reads too.
        Assert.Contains(Members(typeof(Fixture)).OfType<MethodInfo>(), IsDeleteOrMove);
        Assert.DoesNotContain(Members(typeof(ClipFileOps)).OfType<MethodInfo>(), IsDeleteOrMove);
        Assert.Contains(Members(typeof(ClipFileOps)).OfType<MethodInfo>(), m => m.DeclaringType == typeof(IClipFileSystem) && m.Name == nameof(IClipFileSystem.DeleteFile));
        Assert.DoesNotContain(Members(typeof(BookmarkManager)).OfType<MethodInfo>(), m => m.DeclaringType == typeof(IClipFileSystem));
    }

    private static bool IsDeleteOrMove(MethodInfo method)
        => FileSystemTypes.Contains(method.DeclaringType) && Changes.Contains(method.Name);

    private static IEnumerable<Type> PluginTypes() => PluginAssembly.GetTypes()
        .Where(t => !t.IsNested && t.Namespace is { } ns && ns.StartsWith("Jellyfin.Plugin.Bookmarks", StringComparison.Ordinal));

    private static IEnumerable<MemberInfo> Members(Type type) => MethodBodyReferences.DeclaredMethods(type).SelectMany(MethodBodyReferences.FindMembers);

    private static class Fixture
    {
        public static async System.Threading.Tasks.Task DeleteLaterAsync(string path)
        {
            await System.Threading.Tasks.Task.Yield();
            File.Delete(path);
        }
    }
}
