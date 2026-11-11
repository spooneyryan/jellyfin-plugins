using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.Sqlite;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.Logging;
using Jellyfin.Plugin.Kit.TestSupport.Time;
using Jellyfin.Plugin.UserRatings.Domain;
using Jellyfin.Plugin.UserRatings.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jellyfin.Plugin.UserRatings.Tests.Storage;

/// <summary>
/// The store tests share the process-wide SQLite connection pools, so they run one at a time.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class StoreCollection
{
    public const string Name = "UserRatingsStore";
}

/// <summary>
/// <c>userratings.db</c> in a temporary <c>userratings</c> folder through the kit's <see cref="SqliteDatabase"/> on
/// <see cref="RatingsSchema.Migrations"/>, with a manual clock, a logger that keeps every line, and helpers that make
/// synthetic ratings (real SQLite, the server's own native build, in a temp directory).
/// </summary>
internal sealed class StoreFixture : IDisposable
{
    public static readonly DateTime T0 = new(2026, 10, 19, 3, 20, 0, DateTimeKind.Utc);

    private readonly List<SqliteDatabase> _databases = [];

    public StoreFixture()
    {
        Folder = Directory.Combine("userratings");
        System.IO.Directory.CreateDirectory(Folder);
        Database = Open();
        Store = new SqliteRatingsStore(Database, StoreLogger);
    }

    public TempDirectory Directory { get; } = new();

    public ListLogger Logger { get; } = new();

    public ListLogger<SqliteRatingsStore> StoreLogger { get; } = new();

    public ManualTimeProvider Clock { get; } = new(new DateTimeOffset(T0));

    public string Folder { get; }

    public string File => Path.Combine(Folder, RatingsSchema.DatabaseFile);

    public string Snapshots => Path.Combine(Folder, "snapshots");

    public SqliteDatabase Database { get; private set; }

    public SqliteRatingsStore Store { get; private set; }

    // vacuumFault: a VACUUM made to fail (SqliteDatabase.VacuumFault), as on a full disk.
    public SqliteDatabase Open(IReadOnlyList<SqliteMigration>? migrations = null, Action? vacuumFault = null)
    {
        var database = new SqliteDatabase(File, migrations ?? RatingsSchema.Migrations, Logger, Snapshots, Clock) { DamageRecheckDelay = TimeSpan.Zero, VacuumFault = vacuumFault, CheckpointDelay = TimeSpan.FromMilliseconds(10), CheckpointRetryDelays = [TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50)], PreOpKeepWithin = Jellyfin.Plugin.UserRatings.Lifecycle.RatingsUserDataOwner.PreOpKeepWithin, PreOpCopies = [Jellyfin.Plugin.UserRatings.Lifecycle.RatingsLibraryConsumer.StoreName + Jellyfin.Plugin.Kit.Library.DetachedArchive.FileSuffix] };
        _databases.Add(database);
        return database;
    }

    // A second database object on the same file (as after a restart), with these migrations.
    public void Reopen(IReadOnlyList<SqliteMigration>? migrations = null, Action? vacuumFault = null)
    {
        Database.Release("test reopen");
        Database = Open(migrations, vacuumFault);
        Store = new SqliteRatingsStore(Database, StoreLogger);
    }

    public static RatingRecord Movie(Guid user, Guid item, int? stars2 = 7, string? text = null, string tmdb = "603", DateTime? at = null)
        => new()
        {
            Id = Guid.NewGuid(),
            UserId = user,
            Identity = new IdentitySnapshot
            {
                ItemId = item,
                Kind = "Movie",
                Name = "The Matrix",
                Year = 1999,
                ProviderIds = new Dictionary<string, string> { ["Tmdb"] = tmdb, ["Imdb"] = "tt0133093" },
                Path = @"C:\media\The Matrix (1999)\The Matrix (1999).mkv"
            },
            Stars2 = stars2,
            ReviewText = text,
            CreatedUtc = at ?? T0,
            UpdatedUtc = at ?? T0,
            ReviewUpdatedUtc = text is null ? null : at ?? T0
        };

    public static RatingRecord Episode(Guid user, Guid item, Guid series, Guid season, int seasonNumber = 1, int episodeNumber = 2, int? stars2 = 8)
        => new()
        {
            Id = Guid.NewGuid(),
            UserId = user,
            Identity = new IdentitySnapshot
            {
                ItemId = item,
                Kind = "Episode",
                Name = "Episode " + episodeNumber.ToString(CultureInfo.InvariantCulture),
                Year = 2008,
                ProviderIds = new Dictionary<string, string> { ["Tvdb"] = "349232" },
                SeriesProviderIds = new Dictionary<string, string> { ["Tvdb"] = "81189", ["Tmdb"] = "1396" },
                SeriesName = "Breaking Bad",
                Season = seasonNumber,
                Episode = episodeNumber,
                SeriesId = series,
                SeasonId = season
            },
            Stars2 = stars2,
            CreatedUtc = T0,
            UpdatedUtc = T0
        };

    public static RatingRecord Track(Guid user, Guid item, Guid album, int stars2 = 9)
        => new()
        {
            Id = Guid.NewGuid(),
            UserId = user,
            Identity = new IdentitySnapshot { ItemId = item, Kind = "Audio", Name = "Tone 1", AlbumId = album },
            Stars2 = stars2,
            CreatedUtc = T0,
            UpdatedUtc = T0
        };

    public static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public long Count(string table) => Database.Read(c => Scalar(c, "SELECT COUNT(*) FROM " + table + ";"));

    public static long RootPage(SqliteDatabase database, string table)
        => database.Read(c => Scalar(c, "SELECT rootpage FROM sqlite_master WHERE name = '" + table + "';"));

    // Overwrites one page of the database file in place, as a bad sector or a stray write would.
    public static void CorruptPage(string path, long page, int pageSize = 4096)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        stream.Seek((page - 1) * pageSize, SeekOrigin.Begin);
        var garbage = new byte[pageSize];
        Array.Fill(garbage, (byte)0xFF);
        stream.Write(garbage);
        stream.Flush(flushToDisk: true);
    }

    public static byte[] ReadOpenFile(string path)
    {
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var copy = new MemoryStream();
        source.CopyTo(copy);
        return copy.ToArray();
    }

    public static string[] Names(string folder)
        => System.IO.Directory.Exists(folder)
            ? [.. System.Linq.Enumerable.Order(System.Linq.Enumerable.Select(System.IO.Directory.GetFiles(folder), f => Path.GetFileName(f)), StringComparer.Ordinal)]
            : [];

    public void Dispose()
    {
        foreach (var database in _databases)
        {
            database.Dispose();
        }

        Directory.Dispose();
    }
}
