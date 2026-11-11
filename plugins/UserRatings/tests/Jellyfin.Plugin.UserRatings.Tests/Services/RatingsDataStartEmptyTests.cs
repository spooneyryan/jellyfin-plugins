using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Sqlite;
using Jellyfin.Plugin.UserRatings.Import;
using Jellyfin.Plugin.UserRatings.Services;
using Jellyfin.Plugin.UserRatings.Storage;
using Jellyfin.Plugin.UserRatings.Tests.Storage;
using Xunit;

namespace Jellyfin.Plugin.UserRatings.Tests.Services;

/// <summary>
/// Start empty (0.3.19.0): a damaged database with no snapshot that passes had no way on from the Data
/// tab. <c>POST Admin/Data/StartEmpty</c> moves it to a quarantine folder and starts a new empty one, for an admin with every
/// library or an API key, under the runner's hold, with a <c>StartEmpty</c> log row.
/// </summary>
public sealed partial class RatingsDataTests
{
    [Fact]
    public async Task StartEmpty_OnADamagedDatabase_QuarantinesIt_StartsEmptyAndWritable_WithItsLogRow()
    {
        var movie = _s.AddMovie(_s.Movies, "The Matrix", "603");
        var row = await Store.UpsertAsync(Rating(_s.Viewer.Id, movie.Id, "Lost with the damage."), Ct);
        var daily = Path.GetFileName((await _s.Store.Database.SnapshotAsync(3, Ct))!);

        // A database that is fine: 409, nothing moved.
        var fine = await Assert.ThrowsAsync<PluginException>(() => _data.StartEmptyAsync(_s.Admin.Id, Ct));
        Assert.Equal(PluginErrorKind.Conflict, fine.Kind);
        Assert.Equal("Lost with the damage.", Store.Get(row.Id)!.ReviewText);

        Damage(row.Id);
        Assert.Equal(true, Store.DescribeIntegrity()["Damaged"]);

        // An admin without every library: 403 before anything. A job running: 409, nothing moved.
        var restricted = ServicesFixture.NewUser("restricted");
        restricted.SetPermission(PermissionKind.IsAdministrator, true);
        restricted.SetPermission(PermissionKind.EnableAllFolders, false);
        _s.Users.Add(restricted);
        Assert.Equal(RatingsData.AllLibrariesCode, (await Assert.ThrowsAsync<PluginException>(() => _data.StartEmptyAsync(restricted.Id, Ct))).Code);
        using (_jobs.Hold("restore"))
        {
            Assert.Equal(AdminJobRunner.BusyCode, (await Assert.ThrowsAsync<PluginException>(() => _data.StartEmptyAsync(_s.Admin.Id, Ct))).Code);
        }

        Assert.Equal(true, Store.DescribeIntegrity()["Damaged"]);

        var result = await _data.StartEmptyAsync(_s.Admin.Id, Ct);
        Assert.Equal(true, result["StartedEmpty"]);
        var quarantine = Assert.IsType<string>(result["QuarantineFolder"]);
        Assert.StartsWith("quarantine-", quarantine, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(Store.DataDirectory, quarantine, RatingsSchema.DatabaseFile)), "the damaged database is kept");
        Assert.Contains(daily, Store.ListSnapshots().Select(s => s.Name));

        // Empty and writable at once; the log's first row says who started it empty.
        Assert.Equal((false, false), ((bool)Store.DescribeIntegrity()["Damaged"]!, Store.IsReadOnly));
        Assert.Null(Store.Get(row.Id));
        var log = Assert.Single(Store.GetModerationLog(0, 10));
        Assert.Equal((RatingValues.ModerationAction.StartEmpty, (Guid?)_s.Admin.Id), (log.Action, log.ActorUserId));
        Assert.Contains(quarantine, log.Details, StringComparison.Ordinal);
        await Store.UpsertAsync(Rating(_s.Viewer.Id, movie.Id, "A new start."), Ct);

        // Once is enough: the database is fine now.
        Assert.Equal(PluginErrorKind.Conflict, (await Assert.ThrowsAsync<PluginException>(() => _data.StartEmptyAsync(null, Ct))).Kind);
    }

    // The Ratings table's root page overwritten, as the schema tests do; the next read finds it damaged.
    private void Damage(Guid id)
    {
        var root = StoreFixture.RootPage(_s.Store.Database, "Ratings");
        _s.Store.Database.Release("test");
        StoreFixture.CorruptPage(_s.Store.File, root);
        Assert.Throws<PluginException>(() => Store.Get(id));
        Assert.Equal(SqliteDatabaseStatus.ReadOnlyCorrupt, _s.Store.Database.Status);
    }
}
