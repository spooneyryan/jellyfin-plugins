using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Bookmarks.Api;
using Jellyfin.Plugin.Bookmarks.Configuration;
using Jellyfin.Plugin.Bookmarks.Library;
using Jellyfin.Plugin.Bookmarks.Services;
using Jellyfin.Plugin.Bookmarks.Tests.TestSupport;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.TestSupport.Library;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Services;

/// <summary>
/// The developer demo of the kit's library-change monitor and Bookmarks' post-scan task: off by default (403 "turned
/// off", no change collected, full passes dropped), admin-only, and when on it records each batch's flags, ids, kinds and
/// counts, never a name or a path; Bookmarks' post-scan task flushes the registered monitor as one full pass per scan.
/// </summary>
public sealed class LibraryChangeDemoTests
{
    private static readonly Guid User = new("11111111-0000-0000-0000-000000000001");

    private readonly PluginConfiguration _configuration = new() { EnableLibraryChangeDemo = true };
    private readonly FakeLibrary _library = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task IsOffByDefault_TakesNothing_AndEveryCallSaysSo()
    {
        Assert.False(new PluginConfiguration().EnableLibraryChangeDemo);
        _configuration.EnableLibraryChangeDemo = false;
        var demo = CreateDemo();
        var movie = _library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170));

        Assert.False(demo.Filter.Accepts(LibraryChangeKind.Added, movie, BaseItemKind.Movie));
        await demo.HandleAsync(Batch(fullPass: true), Ct);

        foreach (var call in new Action[] { () => demo.Describe(), demo.Clear })
        {
            Assert.Equal(PluginErrorKind.FeatureDisabled, Assert.ThrowsAny<PluginException>(call).Kind);
        }

        _configuration.EnableLibraryChangeDemo = true;
        Assert.Equal(0L, demo.Describe()["Received"]);
    }

    [Fact]
    public void TheController_IsForAdminsOnly()
    {
        var authorize = typeof(BookmarksLibraryChangeDemoController).GetCustomAttribute<AuthorizeAttribute>()!;

        Assert.Equal(Policies.RequiresElevation, authorize.Policy);
    }

    [Fact]
    public void ItsFilter_TakesEverything_WithUserData_Identity_AndTheTopParent_OnAShortTimer()
    {
        var filter = CreateDemo().Filter;

        Assert.Null(filter.Kinds);
        Assert.True(filter.IncludeUserData);
        Assert.True(filter.CaptureIdentity);
        Assert.True(filter.CaptureTopParent);
        Assert.Equal((TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(5)), filter.EffectiveDelays());
        Assert.Equal("LibraryChangeDemo", CreateDemo().Name);
    }

    [Fact]
    public async Task ItRecordsFlagsIdsKindsAndCounts_ButNoNameOrPath()
    {
        var demo = CreateDemo();
        _library.IsScanRunning = true;
        var series = _library.AddSeries("Secret Series Name", @"C:\media\Shows\Secret Series Name");
        var removed = new ItemChange
        {
            Kind = LibraryChangeKind.Removed,
            ItemId = series.Id,
            ItemKind = BaseItemKind.Series,
            IsFolder = true,
            Identity = ItemIdentity.Capture(series, _library.Object),
        };
        var added = new ItemChange { Kind = LibraryChangeKind.Added, ItemId = Guid.NewGuid(), ItemKind = BaseItemKind.Movie, TopParentId = Guid.NewGuid() };
        var updated = new ItemChange { Kind = LibraryChangeKind.Updated, ItemId = Guid.NewGuid(), ItemKind = BaseItemKind.Episode, UpdateReason = ItemUpdateType.MetadataImport };
        var userData = new ItemChange { Kind = LibraryChangeKind.UserData, ItemId = added.ItemId, ItemKind = BaseItemKind.Movie, UserId = User, SaveReason = UserDataSaveReason.UpdateUserRating };

        await demo.HandleAsync(new LibraryChangeBatch([added], [updated], [removed], [removed], [userData], fullPass: true, duringPostScan: true), Ct);

        var described = demo.Describe();
        Assert.Equal(1L, described["Received"]);
        var batch = Assert.Single(Assert.IsType<List<IDictionary<string, object?>>>(described["Batches"]));
        Assert.Equal(true, batch["FullPass"]);
        Assert.Equal(true, batch["DuringPostScan"]);
        Assert.Equal(false, batch["AfterFullPass"]);
        Assert.Equal(true, batch["ScanRunning"]);
        Assert.Equal(["Episode", "Movie", "Series"], Assert.IsType<List<string>>(batch["Kinds"]));
        var removedEntry = Assert.Single(Assert.IsType<List<Dictionary<string, object?>>>(batch["Removed"]));
        var identity = Assert.IsType<Dictionary<string, object?>>(removedEntry["Identity"]);
        Assert.Equal(true, identity["HasName"]);
        Assert.Equal(true, identity["HasPath"]);
        Assert.Equal("Series", identity["Kind"]);
        Assert.Equal(added.TopParentId, Assert.IsType<List<Dictionary<string, object?>>>(batch["Added"]).Single()["TopParentId"]);
        Assert.Equal("MetadataImport", Assert.IsType<List<Dictionary<string, object?>>>(batch["Updated"]).Single()["UpdateReason"]);
        var userEntry = Assert.IsType<List<Dictionary<string, object?>>>(batch["UserData"]).Single();
        Assert.Equal(User, userEntry["UserId"]);
        Assert.Equal("UpdateUserRating", userEntry["Reason"]);

        // Nothing of the item's name or path leaves the demo.
        var json = JsonSerializer.Serialize(described);
        Assert.DoesNotContain("Secret Series Name", json, StringComparison.Ordinal);
        Assert.DoesNotContain("media", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ItKeepsTheLast50Batches_And100ChangesPerList_WithTheFullCounts()
    {
        var demo = CreateDemo();
        var many = Enumerable.Range(0, 150).Select(_ => new ItemChange { Kind = LibraryChangeKind.Added, ItemId = Guid.NewGuid(), ItemKind = BaseItemKind.Movie }).ToList();
        await demo.HandleAsync(new LibraryChangeBatch(many, [], [], [], [], false, false), Ct);
        for (var i = 0; i < 55; i++)
        {
            await demo.HandleAsync(Batch(fullPass: false), Ct);
        }

        var batches = Assert.IsType<List<IDictionary<string, object?>>>(demo.Describe()["Batches"]);
        Assert.Equal(50, batches.Count);
        Assert.Equal(7L, batches[0]["Seq"]);
        Assert.Equal(56L, demo.Describe()["Received"]);

        demo.Clear();
        await demo.HandleAsync(new LibraryChangeBatch(many, [], [], [], [], false, false), Ct);
        var big = Assert.Single(Assert.IsType<List<IDictionary<string, object?>>>(demo.Describe()["Batches"]));
        Assert.Equal(100, Assert.IsType<List<Dictionary<string, object?>>>(big["Added"]).Count);
        Assert.Equal(150, ((Dictionary<string, object?>)big["Counts"]!)["Added"]);
    }

    [Fact]
    public async Task ItRecordsWhetherABatchCameAfterTheScansFullPass()
    {
        var demo = CreateDemo();
        var genre = new ItemChange { Kind = LibraryChangeKind.Added, ItemId = Guid.NewGuid(), ItemKind = BaseItemKind.Genre };

        await demo.HandleAsync(new LibraryChangeBatch([genre], [], [], [], [], fullPass: false, duringPostScan: false, afterFullPass: true), Ct);

        var batch = Assert.Single(Assert.IsType<List<IDictionary<string, object?>>>(demo.Describe()["Batches"]));
        Assert.Equal(true, batch["AfterFullPass"]);
        Assert.Equal(false, batch["FullPass"]);
        Assert.Equal(false, batch["ScanRunning"]);
        Assert.Equal(["Genre"], Assert.IsType<List<string>>(batch["Kinds"]));
    }

    [Fact]
    public async Task BookmarksPostScanTask_FlushesTheRegisteredMonitor_AsOneFullPassPerScan_ToTheDemo()
    {
        var host = new Mock<IServerApplicationHost>();
        host.SetupGet(h => h.ApplicationVersion).Returns(new Version(10, 11, 5));
        host.SetupGet(h => h.CoreStartupHasCompleted).Returns(true);
        using var data = new Kit.TestSupport.TempDirectory();
        var paths = new Mock<MediaBrowser.Common.Configuration.IApplicationPaths>();
        paths.SetupGet(p => p.DataPath).Returns(data.Path);
        var services = new ServiceCollection();
        new PluginServiceRegistrator().RegisterServices(services, host.Object);

        // Every consumer the monitor finds must be creatable, the developer library demo's too (its store is in the data folder).
        services.AddSingleton(host.Object)
            .AddSingleton(_library.Object)
            .AddSingleton(paths.Object)
            .AddSingleton(Mock.Of<IUserDataManager>())
            .AddSingleton(TestData.ConfigurationProvider(_configuration))
            .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<LibraryChangeMonitor>().Subscribe();
        var task = new BookmarksPostScanTask(provider);
        var movie = _library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170));

        _library.IsScanRunning = true;
        _library.RaiseAdded(movie);
        await task.Run(new Progress<double>(), Ct);
        await task.Run(new Progress<double>(), Ct);

        var batches = Assert.IsType<List<IDictionary<string, object?>>>(provider.GetRequiredService<IBookmarkLibraryChangeDemo>().Describe()["Batches"]);
        Assert.Equal(2, batches.Count);
        Assert.All(batches, b => Assert.True((bool)b["FullPass"]! && (bool)b["DuringPostScan"]! && (bool)b["ScanRunning"]!));
        Assert.Equal(movie.Id, Assert.IsType<List<Dictionary<string, object?>>>(batches[0]["Added"]).Single()["ItemId"]);
        Assert.Empty(Assert.IsType<List<Dictionary<string, object?>>>(batches[1]["Added"]));
    }

    [Fact]
    public async Task BookmarksPostScanTask_OnJellyfin12_ReturnsAtOnce()
    {
        var host = new Mock<IServerApplicationHost>();
        host.SetupGet(h => h.ApplicationVersion).Returns(new Version(12, 1, 0));
        var services = new ServiceCollection();
        new PluginServiceRegistrator().RegisterServices(services, host.Object);
        using var provider = services.BuildServiceProvider();

        await new BookmarksPostScanTask(provider).Run(new Progress<double>(), Ct);

        Assert.False(KitAbiGuard.IsActive(provider));
        Assert.Null(provider.GetService<LibraryChangeMonitor>());
    }

    private static LibraryChangeBatch Batch(bool fullPass) => new([], [], [], [], [], fullPass, fullPass);

    private BookmarkLibraryChangeDemo CreateDemo() => new(TestData.ConfigurationProvider(_configuration), _library.Object);
}
