using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.DbConfiguration;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Plugin.SmartSearch.Query.Planning;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.SmartSearch.Tests.Planning;

/// <summary>
/// The planner's own database reads (<see cref="DatabasePushdownFacts"/>: the clean-form probe, the values holding '|', the
/// user-data agreement and the episodes' series names) translated and run by EF Core's SQLite provider at the version
/// 10.11.5 ships, over core's <see cref="JellyfinDbContext"/> model. The fake core implements these facts itself, so only
/// this test (and the live check) runs the real queries: the live check found that <c>string.Contains(char)</c> has no
/// SQLite translation.
/// </summary>
public sealed class DatabasePushdownFactsTests : IDisposable
{
    private static readonly Guid Admin = Guid.NewGuid();

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly Factory _factory;

    public DatabasePushdownFactsTests()
    {
        _connection.Open();
        _factory = new Factory(new DbContextOptionsBuilder<JellyfinDbContext>().UseSqlite(_connection).Options);
        using var context = _factory.CreateDbContext();
        context.Database.EnsureCreated();
        var user = new User("admin", "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider", "Jellyfin.Server.Implementations.Users.DefaultPasswordResetProvider") { Id = Admin };
        context.Users.Add(user);
        var folder = Guid.NewGuid();
        BaseItemEntity Item(string type, string name, string? cleanName = null, Guid? series = null, string? seriesName = null)
        {
            var item = new BaseItemEntity { Id = Guid.NewGuid(), Type = type, Name = name, CleanName = cleanName ?? FakeCore.Clean(name), TopParentId = folder, SeriesId = series, SeriesName = seriesName };
            context.BaseItems.Add(item);
            return item;
        }

        var show = Item("MediaBrowser.Controller.Entities.TV.Series", "Show");
        Item("MediaBrowser.Controller.Entities.TV.Episode", "Pilot", series: show.Id, seriesName: "Show");
        Item("MediaBrowser.Controller.Entities.TV.Episode", "Second", series: show.Id, seriesName: "Old Show");
        var movie = Item("MediaBrowser.Controller.Entities.Movies.Movie", "Brød", cleanName: "brød");
        var other = Item("MediaBrowser.Controller.Entities.Movies.Movie", "Plain");
        Folder = folder;
        foreach (var (type, value, clean) in new[] { (ItemValueType.Genre, "Sci|Fi", "sci|fi"), (ItemValueType.Genre, "Drama", "drama"), (ItemValueType.Tags, "Dräma", "dräma"), (ItemValueType.Artist, "a|b", "a|b") })
        {
            var row = new ItemValue { ItemValueId = Guid.NewGuid(), Type = type, Value = value, CleanValue = clean };
            context.ItemValues.Add(row);
            context.ItemValuesMap.Add(new ItemValueMap { ItemId = movie.Id, ItemValueId = row.ItemValueId, Item = movie, ItemValue = row });
        }

        // Two rows of one item that disagree on Played, one row of another; all agree on IsFavorite.
        context.UserData.Add(new UserData { ItemId = movie.Id, Item = movie, UserId = Admin, User = user, CustomDataKey = "a", Played = true });
        context.UserData.Add(new UserData { ItemId = movie.Id, Item = movie, UserId = Admin, User = user, CustomDataKey = "b", Played = false });
        context.UserData.Add(new UserData { ItemId = other.Id, Item = other, UserId = Admin, User = user, CustomDataKey = "c", Played = true });
        context.SaveChanges();
    }

    private Guid Folder { get; }

    [Fact]
    public void EveryRead_IsTranslatedAndRunsOnSqlite()
    {
        using var facts = new DatabasePushdownFacts(_factory, NullLogger.Instance, TimeProvider.System, null, null);

        var snapshot = facts.Refresh(CancellationToken.None);
        Assert.NotNull(snapshot);
        Assert.Equal(5, snapshot!.ItemsRead);
        Assert.Equal(3, snapshot.ValuesRead);
        Assert.Contains(snapshot.Names, n => n.Name == "Brød");
        Assert.Equal(["Dräma", "Sci|Fi"], snapshot.Values.Select(v => v.Value).Order(StringComparer.Ordinal));

        var pipes = facts.PipeValues(CancellationToken.None);
        Assert.NotNull(pipes);
        Assert.Equal("Sci|Fi", Assert.Single(pipes!).Value);

        Assert.False(facts.UserDataAgrees(Admin, UserDataFlag.Played, CancellationToken.None));
        Assert.True(facts.UserDataAgrees(Admin, UserDataFlag.Favorite, CancellationToken.None));
        Assert.True(facts.UserDataAgrees(Admin, UserDataFlag.Resumable, CancellationToken.None));
        Assert.True(facts.UserDataAgrees(Guid.NewGuid(), UserDataFlag.Played, CancellationToken.None));

        var series = facts.EpisodeSeries([Folder], CancellationToken.None);
        Assert.NotNull(series);
        Assert.Equal(["Old Show", "Show"], series!.Select(s => s.SeriesName).Order(StringComparer.Ordinal));
        Assert.Empty(facts.EpisodeSeries([Guid.NewGuid()], CancellationToken.None)!);
    }

    [Fact]
    public void EveryRead_WithACancelledToken_Throws_InsteadOfAnsweringUnknown()
    {
        using var facts = new DatabasePushdownFacts(_factory, NullLogger.Instance, TimeProvider.System, null, null);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => facts.PipeValues(cancelled.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => facts.UserDataAgrees(Admin, UserDataFlag.Played, cancelled.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => facts.EpisodeSeries([Folder], cancelled.Token));
    }

    [Fact]
    public void TheProbe_PastItsCap_RefusesTheSnapshot_AndCountsEveryStaleRow()
    {
        // More stale values than the probe keeps: it keeps none past the cap but counts them all.
        using (var context = _factory.CreateDbContext())
        {
            for (var i = 0; i < DatabasePushdownFacts.MaxStaleRows + 4; i++)
            {
                context.ItemValues.Add(new ItemValue { ItemValueId = Guid.NewGuid(), Type = ItemValueType.Tags, Value = "Tag " + i, CleanValue = "Tag " + i });
            }

            context.SaveChanges();
        }

        var logger = new ListLogger();
        using var facts = new DatabasePushdownFacts(_factory, logger, TimeProvider.System, null, null);
        Assert.Null(facts.Refresh(CancellationToken.None));
        Assert.Null(facts.CleanForms);
        var warning = Assert.Single(logger.Messages, m => m.Level == Microsoft.Extensions.Logging.LogLevel.Warning);
        Assert.Contains("found 1 item names and " + (DatabasePushdownFacts.MaxStaleRows + 6) + " genre, studio and tag values", warning.Text, StringComparison.Ordinal);
    }

    public void Dispose() => _connection.Dispose();

    private sealed class ListLogger : Microsoft.Extensions.Logging.ILogger
    {
        public List<(Microsoft.Extensions.Logging.LogLevel Level, string Text)> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add((logLevel, formatter(state, exception)));
    }

    private sealed class Factory(DbContextOptions<JellyfinDbContext> options) : IDbContextFactory<JellyfinDbContext>
    {
        public JellyfinDbContext CreateDbContext() => new(options, NullLogger<JellyfinDbContext>.Instance, new Provider(), new NoLocking());
    }

    // Core's SQLite provider adds nothing the facts' queries depend on; the model is JellyfinDbContext's own.
    private sealed class Provider : IJellyfinDatabaseProvider
    {
        public IDbContextFactory<JellyfinDbContext>? DbContextFactory { get; set; }

        public void Initialise(DbContextOptionsBuilder options, DatabaseConfigurationOptions databaseConfiguration)
        {
        }

        public void OnModelCreating(ModelBuilder modelBuilder)
        {
        }

        public void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
        {
        }

        public Task RunScheduledOptimisation(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RunShutdownTask(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<string> MigrationBackupFast(CancellationToken cancellationToken) => Task.FromResult(string.Empty);

        public Task RestoreBackupFast(string key, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DeleteBackup(string key) => Task.CompletedTask;

        public Task PurgeDatabase(JellyfinDbContext dbContext, IEnumerable<string>? tableNames) => Task.CompletedTask;
    }

    private sealed class NoLocking : IEntityFrameworkCoreLockingBehavior
    {
        public void Initialise(DbContextOptionsBuilder optionsBuilder)
        {
        }

        public void OnSaveChanges(JellyfinDbContext context, Action saveChanges) => saveChanges();

        public Task OnSaveChangesAsync(JellyfinDbContext context, Func<Task> saveChanges) => saveChanges();
    }
}
