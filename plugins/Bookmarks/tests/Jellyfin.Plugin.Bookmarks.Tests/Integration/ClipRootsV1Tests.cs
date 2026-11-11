using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bookmarks.Clips;
using Jellyfin.Plugin.Bookmarks.Configuration;
using Jellyfin.Plugin.Bookmarks.Integration;
using Jellyfin.Plugin.Bookmarks.Storage;
using Jellyfin.Plugin.Bookmarks.Tests.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport;
using MediaBrowser.Controller;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Integration;

/// <summary>
/// The clip folders' facade for other plugins (since 1.2.0): <c>ClipRootsV1.Get() → string[]</c>, the clip folders
/// Library Maintenance's Duplicates leaves out of its search. The contract is called as another plugin calls it: located
/// through <see cref="AssemblyLoadContext.All"/> by the exact assembly name and invoked by reflection, with BCL types only, in
/// a load context of the plugin's own (as Jellyfin loads each plugin) and in this process's; the real hosted service is its
/// source after the startup gate, also while the data is read-only, and nothing answers once it stopped. Not run in parallel
/// with other test classes: the facade's source is process-wide.
/// </summary>
[Collection(ClipRootsFacadeCollection.Name)]
public sealed class ClipRootsV1Tests : IDisposable
{
    // The contract's names, as another plugin spells them: it can't reference Bookmarks' types.
    private const string ContractAssembly = "Jellyfin.Plugin.Bookmarks";
    private const string ContractType = "Jellyfin.Plugin.Bookmarks.Integration.ClipRootsV1";
    private const string ContractMethod = "Get";

    private readonly TempDirectory _directory = new();
    private readonly List<IDisposable> _disposables = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (var disposable in Enumerable.Reverse(_disposables))
        {
            disposable.Dispose();
        }

        _directory.Dispose();
    }

    [Fact]
    public void TheFacade_IsPublicStatic_WithOneBclMethod_UnderItsContractName()
    {
        var type = typeof(ClipRootsV1);

        Assert.True(type.IsPublic && type.IsAbstract && type.IsSealed);
        Assert.Equal(ContractType, type.FullName);
        Assert.Equal(ContractAssembly, type.Assembly.GetName().Name);
        var method = Assert.Single(type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly));
        Assert.Equal(ContractMethod, method.Name);
        Assert.Equal(typeof(string[]), method.ReturnType);
        Assert.Empty(method.GetParameters());
        Assert.Empty(type.GetProperties(BindingFlags.Public | BindingFlags.Static));
        Assert.Empty(type.GetFields(BindingFlags.Public | BindingFlags.Static));
    }

    [Fact]
    public void AnotherPlugin_FindsItInBookmarksOwnLoadContext_ByItsExactName_AndCallsItWithBclTypesOnly()
    {
        // Jellyfin loads every plugin into a load context of its own; the plugins share only the framework. Here a
        // second copy of Bookmarks' assembly is loaded so, its dependencies from this process's default context, as Jellyfin's
        // plugin load context resolves shared assemblies.
        var context = new PluginContext(typeof(ClipRootsV1).Assembly.Location);
        try
        {
            var bookmarks = context.LoadFromAssemblyPath(typeof(ClipRootsV1).Assembly.Location);
            Assert.NotSame(typeof(ClipRootsV1).Assembly, bookmarks);

            // Before Bookmarks runs there (its services start after the startup gate) the facade answers nothing, and never throws.
            var get = FindFacade(context) ?? throw new InvalidOperationException("Another plugin couldn't find ClipRootsV1.");
            var before = get();
            Assert.IsType<string[]>(before);
            Assert.Empty(before);

            // Bookmarks' side of that context gives a source (a BCL delegate, whichever context made it): the caller gets a
            // string[] it reads directly, a copy each time.
            var attach = bookmarks.GetType(ContractType)!.GetMethod("Attach", BindingFlags.NonPublic | BindingFlags.Static)!;
            var detach = bookmarks.GetType(ContractType)!.GetMethod("Detach", BindingFlags.NonPublic | BindingFlags.Static)!;
            Func<string[]> source = () => [@"C:\ProgramData\Jellyfin\Server\data\bookmarks\clips", @"E:\Jellyfin Clips"];
            attach.Invoke(null, [source]);
            var folders = get();
            Assert.Equal([@"C:\ProgramData\Jellyfin\Server\data\bookmarks\clips", @"E:\Jellyfin Clips"], folders);
            folders[0] = "changed by the caller";
            Assert.Equal(@"C:\ProgramData\Jellyfin\Server\data\bookmarks\clips", get()[0]);

            // This process's copy is another type with its own state: unaffected.
            Assert.Empty(ClipRootsV1.Get());

            // A source that throws never reaches the caller.
            Func<string[]> broken = () => throw new IOException("The store couldn't be read.");
            attach.Invoke(null, [broken]);
            Assert.Empty(get());
            detach.Invoke(null, [broken]);
            Assert.Empty(get());
        }
        finally
        {
            context.Unload();
        }
    }

    [Fact]
    public async Task TheRealService_IsTheSource_OnceTheServerHasStarted_AnsweringTheRecordedFoldersAndTheCurrentOne_UntilItStops()
    {
        var recorded = _directory.Combine("clips");
        var usb = _directory.Combine("usb clips");
        var configured = _directory.Combine("configured");
        var store = Store(recorded, usb);
        var configuration = new PluginConfiguration { ClipOutputPath = "  " + configured + Path.DirectorySeparatorChar + "  " };
        var started = false;
        var host = new Mock<IServerApplicationHost>();
        host.SetupGet(h => h.CoreStartupHasCompleted).Returns(() => Volatile.Read(ref started));
        var service = new ClipRootsFacadeService(host.Object, store, TestData.ConfigurationProvider(configuration), BookmarkPaths.ForRoot(_directory.Combine("data")), NullLogger<ClipRootsFacadeService>.Instance);
        _disposables.Add(service);
        Assert.Empty(ClipRootsV1.Get());

        await service.StartAsync(Ct);
        try
        {
            // Not before Jellyfin's startup is complete (the startup gate).
            await Task.Delay(300, Ct);
            Assert.Empty(ClipRootsV1.Get());
            Assert.False((bool)ClipRootsFacadeService.Describe()["Answering"]!);

            Volatile.Write(ref started, true);
            var folders = await WaitForAsync(() => ClipRootsV1.Get() is { Length: > 0 } answer ? answer : null);

            // The recorded clip folders, then the configured one, normalised: as another plugin reads them through reflection.
            Assert.Equal([recorded, usb, configured], folders);
            Assert.Equal(folders, FindFacade(AssemblyLoadContext.Default)!());
            var described = ClipRootsFacadeService.Describe();
            Assert.Equal((true, "Jellyfin.Plugin.Bookmarks.Integration.ClipRootsV1.Get"), ((bool)described["Answering"]!, (string)described["Facade"]!));
            Assert.Equal(folders, (string[])described["Folders"]!);
            var calls = ClipRootsV1.Calls;
            ClipRootsFacadeService.Describe();
            Assert.Equal(calls, ClipRootsV1.Calls);

            // Each folder once: the current folder recorded too; the default folder while nothing is configured.
            await store.AddClipRootAsync(configured, Ct);
            Assert.Equal([recorded, usb, configured], ClipRootsV1.Get());
            configuration.ClipOutputPath = string.Empty;
            Assert.Equal([recorded, usb, configured, _directory.Combine("data", "clips")], ClipRootsV1.Get());
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        Assert.Empty(ClipRootsV1.Get());
        Assert.False((bool)ClipRootsFacadeService.Describe()["Answering"]!);
    }

    [Fact]
    public async Task WhileTheDataIsReadOnly_ItStillAnswers_TheFoldersTheFileRecorded()
    {
        // A newer Bookmarks' file: read-only, its clip folders still read. The facade only reads, so it answers.
        Directory.CreateDirectory(_directory.Combine("readonly"));
        var file = _directory.Combine("readonly", "bookmarks.json");
        await File.WriteAllBytesAsync(file, StoreFixtures.Read(StoreFixtures.Schema3), Ct);
        var store = BookmarkStore.Create(file, NullLogger.Instance);
        _disposables.Add(store);
        Assert.True(store.IsReadOnly);
        Assert.NotEmpty(store.ClipRoots);
        var service = Service(store, new PluginConfiguration());
        await service.StartAsync(Ct);
        try
        {
            var folders = await WaitForAsync(() => ClipRootsV1.Get() is { Length: > 0 } answer ? answer : null);
            Assert.Equal(store.ClipRoots.Select(Normalized).Append(_directory.Combine("data", "clips")).Distinct(StringComparer.OrdinalIgnoreCase), folders);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ARestartInTheSameProcess_ReplacesTheSource_AndTheOldServiceStoppingLeavesTheNewOne()
    {
        // Jellyfin 10.11 restarts in the same process with a new host: the new service's source answers, whatever the old
        // one does when it stops.
        var first = Service(Store(_directory.Combine("first")), new PluginConfiguration { ClipOutputPath = _directory.Combine("first") });
        var second = Service(Store(_directory.Combine("second")), new PluginConfiguration { ClipOutputPath = _directory.Combine("second") });
        var firstStopped = false;
        await first.StartAsync(Ct);
        try
        {
            Assert.Equal([_directory.Combine("first")], await WaitForAsync(() => ClipRootsV1.Get() is { Length: > 0 } answer ? answer : null));
            await second.StartAsync(Ct);
            await WaitForAsync(() => ClipRootsV1.Get() is [var only] && only == _directory.Combine("second") ? "replaced" : null);

            await first.StopAsync(CancellationToken.None);
            first.Dispose();
            firstStopped = true;

            Assert.Equal([_directory.Combine("second")], ClipRootsV1.Get());
        }
        finally
        {
            if (!firstStopped)
            {
                await first.StopAsync(CancellationToken.None);
            }

            await second.StopAsync(CancellationToken.None);
        }

        Assert.Empty(ClipRootsV1.Get());
    }

    // How another plugin reaches the facade: every load context's assemblies, the exact assembly name, the type and
    // method by name, BCL types only; null (the feature off) when Bookmarks isn't there. Only the given context's copy here,
    // since this process holds two.
    private static Func<string[]>? FindFacade(AssemblyLoadContext only)
    {
        foreach (var context in AssemblyLoadContext.All)
        {
            if (!ReferenceEquals(context, only))
            {
                continue;
            }

            foreach (var assembly in context.Assemblies)
            {
                if (!string.Equals(assembly.GetName().Name, ContractAssembly, StringComparison.Ordinal))
                {
                    continue;
                }

                var method = assembly.GetType(ContractType, throwOnError: false)?.GetMethod(ContractMethod, BindingFlags.Public | BindingFlags.Static, Type.EmptyTypes);
                if (method?.ReturnType == typeof(string[]))
                {
                    return () => (string[])method.Invoke(null, null)!;
                }
            }
        }

        return null;
    }

    private static string Normalized(string folder) => ClipRootPaths.TryNormalize(folder, out var normalized) ? normalized : folder;

    private static async Task<T> WaitForAsync<T>(Func<T?> found)
        where T : class
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            if (found() is { } value)
            {
                return value;
            }

            if (DateTime.UtcNow > until)
            {
                throw new TimeoutException("The facade didn't answer within 10 s.");
            }

            await Task.Delay(50, Ct);
        }
    }

    private BookmarkStore Store(params string[] clipRoots)
    {
        var store = ClipFileSupport.StoreWithClipRoots(_directory.Combine(Guid.NewGuid().ToString("N"), "bookmarks.json"), clipRoots);
        _disposables.Add(store);
        return store;
    }

    private ClipRootsFacadeService Service(IBookmarkStore store, PluginConfiguration configuration)
    {
        var host = new Mock<IServerApplicationHost>();
        host.SetupGet(h => h.CoreStartupHasCompleted).Returns(true);
        var service = new ClipRootsFacadeService(host.Object, store, TestData.ConfigurationProvider(configuration), BookmarkPaths.ForRoot(_directory.Combine("data")), NullLogger<ClipRootsFacadeService>.Instance);
        _disposables.Add(service);
        return service;
    }

    /// <summary>
    /// A plugin's load context, as Jellyfin gives each plugin one: the plugin's own assembly from its file, everything else
    /// (the framework, Jellyfin's assemblies) from the default context.
    /// </summary>
    private sealed class PluginContext(string pluginPath) : AssemblyLoadContext("bookmarks-contract-" + Path.GetFileNameWithoutExtension(pluginPath), isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName) => null;
    }
}

/// <summary>
/// The tests that give <see cref="ClipRootsV1"/> a source, which is process-wide: never in parallel with other test classes.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ClipRootsFacadeCollection
{
    /// <summary>The collection's name.</summary>
    public const string Name = "ClipRootsV1 source";
}
