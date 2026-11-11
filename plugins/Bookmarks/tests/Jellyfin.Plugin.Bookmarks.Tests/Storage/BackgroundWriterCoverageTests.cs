using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Jellyfin.Plugin.Bookmarks.Clips;
using Jellyfin.Plugin.Bookmarks.Services;
using Jellyfin.Plugin.Bookmarks.Storage;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.Kit.TestSupport.Api;
using Jellyfin.Plugin.Kit.Users;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Storage;

/// <summary>
/// Every read-only status is inert: every part of Bookmarks that writes <c>bookmarks.json</c>
/// either answers a user's request, whose write the read-only store refuses with 503, or works on its own and asks
/// <see cref="BackgroundWriteGate"/> first. The check reads the compiled code (async state machines and lambdas included),
/// so a new part fails here until it does the same. The janitor (the
/// pending-delete retries, the <c>.partial</c> files) asks it, and so do the identity service (backfill, library changes,
/// the move journal, the kit's record owner) and the store's user data owner (the deleted-user policy, the reconciler's
/// names, <i>Remap by name</i>); the maintenance task's own steps go through the kit store's snapshots, which write nothing
/// while read-only, and the clip file operations' retries, which ask it.
/// </summary>
public sealed class BackgroundWriterCoverageTests
{
    private static readonly Assembly PluginAssembly = typeof(Plugin).Assembly;

    /// <summary>
    /// The parts that write only when a user asks, with the requests they serve. A read-only store refuses their writes
    /// with 503, which is what the user should see, so they don't ask the gate.
    /// </summary>
    private static readonly Dictionary<Type, string> RequestWriters = new()
    {
        [typeof(BookmarkManager)] = "creating, changing and deleting bookmarks and their clips (BookmarksController)",
        [typeof(BookmarkPreferencesService)] = "a user's own naming preferences (BookmarksController)",
        [typeof(Jellyfin.Plugin.Bookmarks.Import.JellyfinEnhancedBookmarkImporter)] = "the Jellyfin Enhanced import an admin runs (BookmarksAdminController)",
        [typeof(BookmarkAdminTools)] = "an admin's import of an export and Restore snapshot (BookmarksAdminController)",
        [typeof(ClipFileBatchDelete)] = "a user's delete of several bookmarks with their clips, or of several clips, file first (BookmarksController, BookmarksClipsController), and an admin's of several users' clips (BookmarksAdminClipsController)",
        [typeof(ClipFileRenamer)] = "a user's rename of a clip, its name and its file in one write (BookmarksClipsController)",
        [typeof(ClipFileSingleDelete)] = "a user's delete of one clip, file first, or of a bookmark with its clip (BookmarksController, moved out of the manager in 1.2.0), and an admin's of one user's clip (BookmarksAdminClipsController)",
    };

    // The parts Jellyfin or the host runs on their own: a hosted service, a scheduled or post-scan task, the kit's users
    // services calling an owner, the kit's library-change monitor calling a consumer.
    private static readonly Type[] RunOnTheirOwn = [typeof(IHostedService), typeof(IScheduledTask), typeof(ILibraryPostScanTask), typeof(IUserDataOwner)];

    /// <summary>
    /// The parts that run on their own and only read the bookmark store, with what they read. They write nothing (the test
    /// checks it), so read-only data needs nothing of them, and they go on answering then.
    /// </summary>
    private static readonly Dictionary<Type, string> Readers = new()
    {
        [typeof(Jellyfin.Plugin.Bookmarks.Integration.ClipRootsFacadeService)] = "the recorded clip folders, for the ClipRootsV1 facade",
    };

    [Fact]
    public void EveryWriterOfTheBookmarkFile_ServesRequests_OrAsksTheGate()
    {
        var writers = PluginTypes().Where(t => t != typeof(BookmarkStore) && WritesTheStore(t)).ToList();

        var ungated = writers.Where(t => !RequestWriters.ContainsKey(t) && !AsksTheGate(t)).Select(t => t.FullName).ToList();
        Assert.True(
            ungated.Count == 0,
            "These write bookmarks.json without asking BackgroundWriteGate.MayWrite first; a part that only serves requests goes in RequestWriters: "
                + string.Join(", ", ungated));

        // Today: the manager (requests), the clip worker (on its own; its EnqueueAsync and CancelAsync serve the manager),
        // the clip file operations, whose automatic deletes list files in PendingFileDeletes and whose retries
        // drop them: they ask the gate for the writer that calls them (the worker, the janitor, the maintenance and the
        // deleted-user policy); the identity service and the store's user data owner; the users' naming
        // preferences (requests); the Jellyfin Enhanced import (an admin's request); the settings page's
        // import and Restore snapshot (an admin's requests); since 1.2.0 the file-first bulk delete the manager's bulk
        // delete with the clips and the clip service's share (requests; the clip service itself writes only through it);
        // the missing-clip sweep of the daily maintenance (on its own: it asks the gate for the maintenance); the
        // clip renamer (requests; the clip service composes it), while the clip file operations' finishing of renames a stop
        // left half done asks the gate for the janitor and the maintenance that call it; and the file-first single
        // delete, moved out of the manager so the admins' delete shares it (requests; the admins' clip service writes only
        // through it, the bulk delete and the clip worker's cancel).
        Assert.Equal(
            [
                typeof(ClipExtractionService).FullName, typeof(ClipFileBatchDelete).FullName, typeof(ClipFileOps).FullName, typeof(ClipFileRenamer).FullName,
                typeof(ClipFileSingleDelete).FullName, typeof(MissingClipSweep).FullName, typeof(Jellyfin.Plugin.Bookmarks.Import.JellyfinEnhancedBookmarkImporter).FullName,
                typeof(BookmarkAdminTools).FullName, typeof(BookmarkIdentityService).FullName, typeof(BookmarkManager).FullName,
                typeof(BookmarkPreferencesService).FullName, typeof(BookmarkUserDataOwner).FullName
            ],
            writers.Select(t => t.FullName).Order(StringComparer.Ordinal));
        Assert.False(AsksTheGate(typeof(ClipFileBatchDelete)));
        Assert.False(AsksTheGate(typeof(ClipFileRenamer)));
        Assert.False(AsksTheGate(typeof(ClipFileSingleDelete)));
        Assert.False(WritesTheStore(typeof(ClipService)));
        Assert.False(WritesTheStore(typeof(AdminClipService)));
        Assert.True(AsksTheGate(typeof(ClipExtractionService)));
        Assert.True(AsksTheGate(typeof(ClipFileOps)));
        Assert.True(AsksTheGate(typeof(PartialFileJanitor)));
        Assert.True(AsksTheGate(typeof(BookmarkIdentityService)));
        Assert.True(AsksTheGate(typeof(BookmarkUserDataOwner)));
        Assert.True(AsksTheGate(typeof(MissingClipSweep)));
        Assert.False(AsksTheGate(typeof(BookmarkManager)));
        Assert.False(AsksTheGate(typeof(BookmarkPreferencesService)));
        Assert.All(RequestWriters.Keys, t => Assert.Contains(t, writers));
    }

    [Fact]
    public void EveryPartThatRunsOnItsOwn_AndUsesTheBookmarkStore_AsksTheGate()
    {
        var own = PluginTypes()
            .Where(t => RunOnTheirOwn.Any(i => i.IsAssignableFrom(t)) || t.GetInterfaces().Any(i => i.Name == "ILibraryChangeConsumer"))
            .ToList();

        var ungated = own.Where(t => UsesTheStore(t) && !AsksTheGate(t) && !Readers.ContainsKey(t)).Select(t => t.FullName).ToList();

        Assert.True(ungated.Count == 0, "These run on their own and use the bookmark store without asking BackgroundWriteGate: " + string.Join(", ", ungated));

        // The developer user data demo's owner keeps its own file (userdata-demo.json) and reports that store's IsReadOnly.
        Assert.Contains(typeof(ClipExtractionService), own);
        Assert.Contains(typeof(BookmarkIdentityService), own);
        Assert.Contains(typeof(BookmarkUserDataOwner), own);
        Assert.Contains(typeof(UserDataDemoOwner), own);
        Assert.False(UsesTheStore(typeof(UserDataDemoOwner)));

        // A reader runs on its own, reads the store and writes nothing (the ClipRootsV1 facade's source answers while read-only too).
        Assert.All(Readers.Keys, t =>
        {
            Assert.Contains(t, own);
            Assert.True(UsesTheStore(t), t.FullName + " no longer reads the store: take it off Readers.");
            Assert.False(WritesTheStore(t), t.FullName + " writes the store: it must ask the gate.");
        });
    }

    // The plugin's own types, outermost only (their methods include nested state machines and closures); the kit's source
    // compiled into the assembly writes no bookmark data.
    private static IEnumerable<Type> PluginTypes() => PluginAssembly.GetTypes()
        .Where(t => !t.IsNested && t.Namespace is { } ns && ns.StartsWith("Jellyfin.Plugin.Bookmarks", StringComparison.Ordinal))
        .Where(t => t.IsClass);

    // Every asynchronous member of IBookmarkStore is a write (its reads are synchronous), and so are the kit store's
    // UpdateAsync and RestoreSnapshotAsync, reachable through IBookmarkStore.Documents.
    private static bool WritesTheStore(Type type) => Members(type).OfType<MethodInfo>().Any(IsWrite);

    private static bool IsWrite(MethodInfo method)
    {
        if (method.DeclaringType == typeof(IBookmarkStore) || method.DeclaringType == typeof(BookmarkStore))
        {
            return typeof(System.Threading.Tasks.Task).IsAssignableFrom(method.ReturnType);
        }

        return method.DeclaringType == typeof(JsonDocumentStore<BookmarkStoreDocument>)
            && method.Name is nameof(JsonDocumentStore<BookmarkStoreDocument>.UpdateAsync) or nameof(JsonDocumentStore<BookmarkStoreDocument>.RestoreSnapshotAsync);
    }

    private static bool AsksTheGate(Type type) => Members(type).OfType<MethodInfo>()
        .Any(m => m.DeclaringType == typeof(BackgroundWriteGate) && m.Name == nameof(BackgroundWriteGate.MayWrite));

    private static bool UsesTheStore(Type type)
        => MethodBodyReferences.DeclaredMethods(type).SelectMany(MethodBodyReferences.FindTypes)
            .Concat(MethodBodyReferences.DeclaredFields(type).Select(f => f.FieldType))
            .Any(t => t == typeof(IBookmarkStore) || t == typeof(BookmarkStore) || t == typeof(JsonDocumentStore<BookmarkStoreDocument>));

    private static IEnumerable<MemberInfo> Members(Type type) => MethodBodyReferences.DeclaredMethods(type).SelectMany(MethodBodyReferences.FindMembers);
}
