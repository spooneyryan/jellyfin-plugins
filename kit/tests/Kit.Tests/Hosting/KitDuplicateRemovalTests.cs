using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.PluginIdentity;
using Jellyfin.Plugin.Kit.Tests.TestHosting;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Kit.Tests.Hosting;

/// <summary>
/// KitDuplicateRemoval: once the server has started, the highest copy has Jellyfin's plugin manager turn off each loaded
/// lower copy and tells the server a restart is pending, removes each lower copy that isn't loaded (only those, only when
/// they are still copies of the plugin) or keeps it turned off while its DLL is in use, and never throws. The mock plugin
/// manager deletes folders and writes the Disabled status to disk as Jellyfin does.
/// </summary>
public sealed class KitDuplicateRemovalTests : IDisposable
{
    private const string Dll = "Jellyfin.Plugin.KitTest.dll";
    private readonly PluginCopies _copies = new();
    private readonly Mock<IPluginManager> _manager = new();
    private readonly Mock<IServerApplicationHost> _host = new();
    private readonly List<LocalPlugin> _removed = [];
    private readonly List<LocalPlugin> _disabled = [];

    public KitDuplicateRemovalTests()
    {
        _manager.Setup(m => m.RemovePlugin(It.IsAny<LocalPlugin>())).Returns(true).Callback<LocalPlugin>(p =>
        {
            _removed.Add(p);
            Directory.Delete(p.Path, recursive: true);
        });
        _manager.Setup(m => m.DisablePlugin(It.IsAny<LocalPlugin>())).Callback<LocalPlugin>(p =>
        {
            _disabled.Add(p);
            PluginCopies.WriteManifest(p.Path, p.Id, p.Name, p.Version.ToString(), status: "Disabled");
        });
    }

    public void Dispose() => _copies.Dispose();

    [Fact]
    public async Task TurnsOffTheLoadedLowerCopy_AndNotifiesThePendingRestart_OnceTheServerHasStarted()
    {
        var verdict = Layout(out var own, out var lower);
        var other = _copies.AddBuild("Other_1.0.0.0", "Jellyfin.Plugin.Other.dll", Guid.NewGuid(), new Version(1, 0));
        List(Entry(own, TestKit.PluginId), Entry(lower, TestKit.PluginId), Entry(other, TestKit.PluginId), Entry(lower, Guid.NewGuid()));
        var lifetime = new ApplicationLifetime(NullLogger<ApplicationLifetime>.Instance);

        var removal = Create(verdict, lifetime);
        await removal.StartAsync(CancellationToken.None);
        Assert.Empty(_disabled);

        lifetime.NotifyStarted();

        Assert.Equal(lower, Assert.Single(_disabled).Path);
        Assert.Empty(_removed);
        Assert.Equal(PluginStatus.Disabled, KitDuplicateGuard.ReadStatus(lower));
        _host.Verify(h => h.NotifyPendingRestart(), Times.Once);
    }

    [Theory]
    [InlineData("Disabled")]
    [InlineData("Superseded")]
    public void RemovesALowerCopyThatIsntLoaded_WithoutARestart(string status)
    {
        var verdict = Layout(out var own, out var lower, status);
        Assert.False(Assert.Single(verdict.ToRemove).IsLoaded);
        Assert.False(verdict.StandsDown);
        List(Entry(own, TestKit.PluginId), Entry(lower, TestKit.PluginId));

        Create(verdict).Run();

        Assert.Equal(lower, Assert.Single(_removed).Path);
        Assert.Empty(_disabled);
        _host.Verify(h => h.NotifyPendingRestart(), Times.Never);
    }

    [Fact]
    public void ALowerCopyWhoseDllIsInUse_StaysTurnedOff_InsteadOfBeingMarkedDeleted()
    {
        // As after a restart from the Dashboard, which runs in the same process: the DLL loaded before it is still locked.
        var verdict = Layout(out var own, out var lower, "Superseded");
        List(Entry(own, TestKit.PluginId), Entry(lower, TestKit.PluginId));
        using (new FileStream(Path.Combine(lower, Dll), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Create(verdict).Run();
        }

        if (OperatingSystem.IsWindows())
        {
            Assert.Empty(_removed);
            Assert.Equal(lower, Assert.Single(_disabled).Path);
            Assert.Equal(PluginStatus.Disabled, KitDuplicateGuard.ReadStatus(lower));
        }
        else
        {
            Assert.Equal(lower, Assert.Single(_removed).Path);
        }

        _host.Verify(h => h.NotifyPendingRestart(), Times.Never);
    }

    [Fact]
    public void WhenJellyfinOnlyMarksTheCopyDeleted_ItIsTurnedOffInstead()
    {
        // Jellyfin's catalog refresh sets a Deleted copy Active again; a Disabled one stays Disabled.
        var verdict = Layout(out var own, out var lower, "Disabled");
        List(Entry(own, TestKit.PluginId), Entry(lower, TestKit.PluginId));
        _manager.Setup(m => m.RemovePlugin(It.IsAny<LocalPlugin>())).Returns(true).Callback<LocalPlugin>(p =>
        {
            _removed.Add(p);
            PluginCopies.WriteManifest(p.Path, p.Id, p.Name, p.Version.ToString(), status: "Deleted");
        });

        Create(verdict).Run();

        Assert.Single(_removed);
        Assert.Single(_disabled);
        Assert.Equal(PluginStatus.Disabled, KitDuplicateGuard.ReadStatus(lower));
        _host.Verify(h => h.NotifyPendingRestart(), Times.Never);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ALeftoverWithoutMetaJsonTheServerDropped_IsGivenADisabledOne_OrDeleted(bool inUse)
    {
        // What Jellyfin leaves of a folder it could delete only in part: the locked DLL, no meta.json.
        var own = _copies.Folder("Kit Test_2.5.2.0");
        PluginCopies.WriteAssembly(Path.Combine(own, Dll), "Jellyfin.Plugin.KitTest", new Version(2, 5, 2, 0), hasGuard: true);
        PluginCopies.WriteManifest(own, TestKit.PluginId, "Kit Test", "2.5.2.0");
        var leftover = _copies.Folder("Kit Test_2.5.1.0");
        PluginCopies.WriteAssembly(Path.Combine(leftover, Dll), "Jellyfin.Plugin.KitTest", new Version(2, 5, 1, 0), hasGuard: true);
        var verdict = KitDuplicateGuard.Inspect("Kit Test", Path.Combine(own, Dll), TestKit.PluginId, JellyfinLine.Server, true, JellyfinLine.Is12, _ => false);
        List(Entry(own, TestKit.PluginId));

        using (inUse ? new FileStream(Path.Combine(leftover, Dll), FileMode.Open, FileAccess.Read, FileShare.Read) : null)
        {
            Create(verdict).Run();
        }

        if (inUse && OperatingSystem.IsWindows())
        {
            var copy = KitDuplicateGuard.ReadCopy(leftover, Dll, TestKit.PluginId, JellyfinLine.Server, isOwn: false);
            Assert.NotNull(copy);
            Assert.True(copy.HasManifest);
            Assert.Equal(PluginStatus.Disabled, copy.Status);
            Assert.Equal(new Version(2, 5, 1, 0), copy.Version);
            Assert.Contains("\"name\": \"Kit Test\"", File.ReadAllText(Path.Combine(leftover, "meta.json")), StringComparison.Ordinal);
        }
        else
        {
            Assert.False(Directory.Exists(leftover));
        }

        Assert.Empty(_removed);
        Assert.Empty(_disabled);
        _host.Verify(h => h.NotifyPendingRestart(), Times.Never);
    }

    [Fact]
    public void AFolderThatIsNoLongerACopy_IsLeftAlone()
    {
        var verdict = Layout(out var own, out var lower);
        List(Entry(own, TestKit.PluginId), Entry(lower, TestKit.PluginId));
        File.Delete(Path.Combine(lower, Dll));

        Create(verdict).Run();

        Assert.Empty(_disabled);
        Assert.Empty(_removed);
        _host.Verify(h => h.NotifyPendingRestart(), Times.Never);
    }

    [Fact]
    public void AFolderWithoutMetaJson_IsMatchedByItsFolder()
    {
        var own = _copies.Folder("Kit Test And More_2.5.2.0");
        PluginCopies.WriteAssembly(Path.Combine(own, Dll), "Jellyfin.Plugin.KitTest", new Version(2, 5, 2, 0), hasGuard: true);
        PluginCopies.WriteManifest(own, TestKit.PluginId, "Kit Test And More", "2.5.2.0");
        var lower = _copies.Folder("KitTest");
        PluginCopies.WriteAssembly(Path.Combine(lower, Dll), "Jellyfin.Plugin.KitTest", new Version(2, 4, 0, 0), hasGuard: false);
        var verdict = KitDuplicateGuard.Inspect("Kit Test", Path.Combine(own, Dll), TestKit.PluginId, JellyfinLine.Server, true, JellyfinLine.Is12);
        List(Entry(own, TestKit.PluginId), Entry(lower, Guid.NewGuid()));

        Create(verdict).Run();

        Assert.Equal(lower, Assert.Single(_disabled).Path);
        _host.Verify(h => h.NotifyPendingRestart(), Times.Once);
    }

    [Fact]
    public void ACopyTheServerDoesntList_IsLeftAlone()
    {
        var verdict = Layout(out var own, out _);
        List(Entry(own, TestKit.PluginId));

        Create(verdict).Run();

        Assert.Empty(_disabled);
        Assert.Empty(_removed);
        _host.Verify(h => h.NotifyPendingRestart(), Times.Never);
    }

    [Fact]
    public void WhenTheDisabledStatusIsntSaved_NoRestartIsNotified()
    {
        // Jellyfin sets the status in memory before it tries to save the file.
        var verdict = Layout(out var own, out var lower);
        List(Entry(own, TestKit.PluginId), Entry(lower, TestKit.PluginId));
        _manager.Setup(m => m.DisablePlugin(It.IsAny<LocalPlugin>())).Callback<LocalPlugin>(p => p.Manifest.Status = PluginStatus.Disabled);

        Create(verdict).Run();

        _host.Verify(h => h.NotifyPendingRestart(), Times.Never);
    }

    [Fact]
    public async Task NeverThrows()
    {
        var verdict = Layout(out var own, out var lower);
        List(Entry(own, TestKit.PluginId), Entry(lower, TestKit.PluginId));
        _manager.Setup(m => m.DisablePlugin(It.IsAny<LocalPlugin>())).Throws(new IOException("Locked."));

        await Create(verdict).StartAsync(CancellationToken.None);

        var withoutManager = new KitDuplicateRemoval(new ServiceCollection().BuildServiceProvider(), _host.Object, TestKit.Options, verdict.Own!, Dll, verdict.ToRemove);
        await withoutManager.StartAsync(CancellationToken.None);
        _host.Verify(h => h.NotifyPendingRestart(), Times.Never);
    }

    [Fact]
    public void OnJellyfin10_11_TheCatalogRefreshAfterTheTurnOff_KeepsTheCopyDisabled()
    {
        // Jellyfin 10.11.5: DisablePlugin saves Disabled, then ProcessAlternative sets the status to Restart in memory, and
        // the catalog refresh (PopulateManifest) writes Disabled when the in-memory status is Disabled, else Active.
        var verdict = Layout(out var own, out var lower);
        var entry = Entry(lower, TestKit.PluginId);
        List(Entry(own, TestKit.PluginId), entry);
        _manager.Setup(m => m.DisablePlugin(It.IsAny<LocalPlugin>())).Callback<LocalPlugin>(p =>
        {
            _disabled.Add(p);
            p.Manifest.Status = PluginStatus.Disabled;
            PluginCopies.WriteManifest(p.Path, p.Id, p.Name, p.Version.ToString(), status: "Disabled");
            p.Manifest.Status = PluginStatus.Restart;
        });

        Create(verdict).Run();
        var refreshed = entry.Manifest.Status == PluginStatus.Disabled ? "Disabled" : "Active";
        PluginCopies.WriteManifest(lower, TestKit.PluginId, "Kit Test", "2.5.0.0", refreshed);

        Assert.Single(_disabled);
        Assert.Equal(PluginStatus.Disabled, KitDuplicateGuard.ReadStatus(lower));
        _host.Verify(h => h.NotifyPendingRestart(), Times.Once);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnotherPluginWithTheSameDllFileName_IsNeverTurnedOffDeletedOrRewritten(bool writesItsMetaJson)
    {
        // Another publisher's plugin, dropped in by hand without meta.json, so the guard counts it as a copy at registration;
        // Jellyfin then creates its plugin under its own id and may save a meta.json naming that id.
        var own = _copies.Folder("Kit Test And More_2.5.2.0");
        PluginCopies.WriteAssembly(Path.Combine(own, Dll), "Jellyfin.Plugin.KitTest", new Version(2, 5, 2, 0), hasGuard: true);
        PluginCopies.WriteManifest(own, TestKit.PluginId, "Kit Test And More", "2.5.2.0");
        var foreign = _copies.Folder("SomeRatings");
        PluginCopies.WriteAssembly(Path.Combine(foreign, Dll), "Jellyfin.Plugin.KitTest", new Version(1, 0, 0, 0), hasGuard: false);
        var verdict = KitDuplicateGuard.Inspect("Kit Test", Path.Combine(own, Dll), TestKit.PluginId, JellyfinLine.Server, true, JellyfinLine.Is12);
        Assert.Equal("SomeRatings", Assert.Single(verdict.ToRemove).FolderName);
        var foreignId = Guid.NewGuid();
        if (writesItsMetaJson)
        {
            PluginCopies.WriteManifest(foreign, foreignId, "Some Ratings", "1.0.0.0");
        }

        var manifest = Path.Combine(foreign, "meta.json");
        var before = File.Exists(manifest) ? File.ReadAllText(manifest) : null;
        var entry = Entry(foreign, foreignId);
        entry.Instance = Mock.Of<IPlugin>();
        List(Entry(own, TestKit.PluginId), entry);

        Create(verdict).Run();

        Assert.Empty(_disabled);
        Assert.Empty(_removed);
        Assert.True(File.Exists(Path.Combine(foreign, Dll)));
        Assert.Equal(before, File.Exists(manifest) ? File.ReadAllText(manifest) : null);
        _host.Verify(h => h.NotifyPendingRestart(), Times.Never);
    }

    [Fact]
    public void AnotherPluginWithTheSameDllFileNameAndItsOwnMetaJson_IsNeverACopy()
    {
        var own = _copies.Folder("Kit Test And More_2.5.2.0");
        PluginCopies.WriteAssembly(Path.Combine(own, Dll), "Jellyfin.Plugin.KitTest", new Version(2, 5, 2, 0), hasGuard: true);
        PluginCopies.WriteManifest(own, TestKit.PluginId, "Kit Test And More", "2.5.2.0");
        var foreignId = Guid.NewGuid();
        var foreign = _copies.AddBuild("SomeRatings_1.0.0.0", Dll, foreignId, new Version(1, 0));
        var before = File.ReadAllText(Path.Combine(foreign, "meta.json"));

        var verdict = KitDuplicateGuard.Inspect("Kit Test", Path.Combine(own, Dll), TestKit.PluginId, JellyfinLine.Server, true, JellyfinLine.Is12);
        List(Entry(own, TestKit.PluginId), Entry(foreign, foreignId));
        new KitDuplicateRemoval(new ServiceCollection().AddSingleton(_manager.Object).BuildServiceProvider(), _host.Object, TestKit.Options, verdict.Own!, Dll, verdict.ToRemove).Run();

        Assert.False(verdict.StandsDown);
        Assert.Empty(verdict.ToRemove);
        Assert.Empty(_disabled);
        Assert.Empty(_removed);
        Assert.Equal(before, File.ReadAllText(Path.Combine(foreign, "meta.json")));
    }

    [Fact]
    public void OneCopyThatFails_DoesntStopTheOthers()
    {
        var own = _copies.Folder("Kit Test And More_2.5.2.0");
        PluginCopies.WriteAssembly(Path.Combine(own, Dll), "Jellyfin.Plugin.KitTest", new Version(2, 5, 2, 0), hasGuard: true);
        PluginCopies.WriteManifest(own, TestKit.PluginId, "Kit Test And More", "2.5.2.0");
        var first = _copies.AddBuild("Kit Test_2.5.1.0", Dll, TestKit.PluginId, new Version(2, 5, 1, 0));
        var second = _copies.AddBuild("Kit Test_2.5.0.0", Dll, TestKit.PluginId, new Version(2, 5, 0, 0));
        var verdict = KitDuplicateGuard.Inspect("Kit Test", Path.Combine(own, Dll), TestKit.PluginId, JellyfinLine.Server, true, JellyfinLine.Is12);
        Assert.Equal(2, verdict.ToRemove.Count);
        List(Entry(own, TestKit.PluginId), Entry(first, TestKit.PluginId), Entry(second, TestKit.PluginId));
        _manager.Setup(m => m.DisablePlugin(It.IsAny<LocalPlugin>())).Callback<LocalPlugin>(p =>
        {
            if (p.Path == first)
            {
                throw new UnauthorizedAccessException("Not writable.");
            }

            _disabled.Add(p);
            PluginCopies.WriteManifest(p.Path, p.Id, p.Name, p.Version.ToString(), status: "Disabled");
        });

        Create(verdict).Run();

        Assert.Equal(second, Assert.Single(_disabled).Path);
        Assert.Equal(PluginStatus.Active, KitDuplicateGuard.ReadStatus(first));
        _host.Verify(h => h.NotifyPendingRestart(), Times.Once);
    }

    [Fact]
    public void RunsOnce()
    {
        var verdict = Layout(out var own, out var lower);
        List(Entry(own, TestKit.PluginId), Entry(lower, TestKit.PluginId));
        var removal = Create(verdict);

        removal.Run();
        removal.Run();

        Assert.Single(_disabled);
    }

    private KitDuplicateVerdict Layout(out string own, out string lower, string status = "Active")
    {
        own = _copies.Folder("Kit Test And More_2.5.2.0");
        PluginCopies.WriteAssembly(Path.Combine(own, Dll), "Jellyfin.Plugin.KitTest", new Version(2, 5, 2, 0), hasGuard: true);
        PluginCopies.WriteManifest(own, TestKit.PluginId, "Kit Test And More", "2.5.2.0");
        lower = _copies.AddBuild("Kit Test_2.5.0.0", Dll, TestKit.PluginId, new Version(2, 5, 0, 0));
        PluginCopies.WriteManifest(lower, TestKit.PluginId, "Kit Test", "2.5.0.0", status);
        var verdict = KitDuplicateGuard.Inspect("Kit Test", Path.Combine(own, Dll), TestKit.PluginId, JellyfinLine.Server, true, JellyfinLine.Is12);
        Assert.Single(verdict.ToRemove);
        return verdict;
    }

    private void List(params LocalPlugin[] plugins) => _manager.SetupGet(m => m.Plugins).Returns(plugins);

    private static LocalPlugin Entry(string folder, Guid id)
        => new(folder, true, new PluginManifest { Id = id, Name = Path.GetFileName(folder), Version = "2.5.0.0" });

    private KitDuplicateRemoval Create(KitDuplicateVerdict verdict, IHostApplicationLifetime? lifetime = null)
    {
        var services = new ServiceCollection().AddSingleton(_manager.Object);
        if (lifetime is not null)
        {
            services.AddSingleton(lifetime);
        }

        return new KitDuplicateRemoval(services.BuildServiceProvider(), _host.Object, TestKit.Options, verdict.Own!, Dll, verdict.ToRemove);
    }
}
