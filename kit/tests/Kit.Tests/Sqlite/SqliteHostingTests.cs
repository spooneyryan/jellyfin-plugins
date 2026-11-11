using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Diagnostics;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.Sqlite;
using Jellyfin.Plugin.Kit.TestSupport.Api;
using Jellyfin.Plugin.Kit.TestSupport.Logging;
using Jellyfin.Plugin.Kit.Tests.TestHosting;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Kit.Tests.Sqlite;

/// <summary>
/// How a plugin hosts a <see cref="SqliteDatabase"/>: the SQLite isolation rule checked on the compiled code, the
/// registration, the hosted service's release at shutdown, the maintenance steps, Diagnostics, and a database that
/// can't be opened (nothing moved, tried again).
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed partial class SqliteHostingTests : IDisposable
{
    private readonly SqliteFixture _fx = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _fx.Dispose();

    [Fact]
    public void Isolation_NoMemberWithoutASqliteSignature_NamesASqliteType_UnlessItIsNeverInlined()
    {
        // The isolation rule: a plugin built against a Microsoft.Data.Sqlite the server can't bind fails when the JIT
        // compiles a method that names one of its types. So the constructor, the fields and every member a DI-built
        // service calls (no SQLite type in their signature) name none; SQLite work sits in NoInlining methods called
        // inside a catch.
        var methods = MethodBodyReferences.DeclaredMethods(typeof(SqliteDatabase)).ToList();
        var allowed = methods
            .Where(m => !IsGenerated(m) && (IsNoInlining(m) || SignatureNamesSqlite(m)))
            .Select(m => m.Name)
            .ToHashSet(StringComparer.Ordinal);

        var offenders = methods
            .Where(m => MethodBodyReferences.FindTypes(m).Any(IsSqlite) && !allowed.Contains(Owner(m)))
            .Select(m => m.DeclaringType!.Name + "." + m.Name)
            .ToList();

        Assert.Empty(offenders);
        Assert.DoesNotContain(typeof(SqliteDatabase).GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public), f => MethodBodyReferences.Expand(f.FieldType).Any(IsSqlite));
        Assert.All(typeof(SqliteDatabase).GetConstructors(), c => Assert.DoesNotContain(MethodBodyReferences.FindTypes(c), IsSqlite));

        // Not vacuous: the upgrade does touch SQLite and is never inlined; what Diagnostics and the hosted service call doesn't.
        var migrate = methods.Single(m => m.Name == "Migrate");
        Assert.True(IsNoInlining(migrate));
        Assert.Contains(MethodBodyReferences.FindTypes(migrate), IsSqlite);
        Assert.True(IsNoInlining(methods.Single(m => m.Name == "OpenCore")));
        foreach (var name in new[] { "Open", "Describe", "Release", "Dispose", "get_Status", "get_IsReadOnly", "QuickCheck", "ListSnapshots", "ListQuarantines", "EnsureOpen" })
        {
            Assert.All(methods.Where(m => m.Name == name), m => Assert.DoesNotContain(MethodBodyReferences.FindTypes(m), IsSqlite));
        }

        // The async members a maintenance step or an admin endpoint calls, state machines included.
        foreach (var name in new[] { "SnapshotAsync", "PreOpSnapshotAsync", "RestoreSnapshotAsync", "RecoverAsync" })
        {
            Assert.All(methods.Where(m => Owner(m) == name), m => Assert.DoesNotContain(MethodBodyReferences.FindTypes(m), IsSqlite));
        }
    }

    [Fact]
    public void Isolation_TheHostedServiceAndTheRegistration_NameNoSqliteTypeAtAll()
    {
        foreach (var type in new[] { typeof(SqliteDatabaseService), typeof(KitSqliteServiceCollectionExtensions), typeof(SqliteMigration), typeof(SqliteFiles) })
        {
            var offenders = MethodBodyReferences.DeclaredMethods(type).Where(m => MethodBodyReferences.FindTypes(m).Any(IsSqlite)).Select(m => m.DeclaringType!.Name + "." + m.Name).ToList();
            Assert.Empty(offenders);
        }
    }

    [Fact]
    public async Task AddKitSqliteDatabase_RegistersTheDatabase_ItsService_Diagnostics_AndTheMaintenanceSteps()
    {
        var provider = Provider(services => services.AddKitSqliteDatabase("userratings.db", SqliteFixture.V2, _ => 2));

        var database = provider.GetRequiredService<SqliteDatabase>();
        Assert.Equal(Path.Combine(_fx.Directory.Path, "kittest", "userratings.db"), database.FilePath);
        Assert.Equal(Path.Combine(_fx.Directory.Path, "kittest", "snapshots"), database.SnapshotDirectory);
        Assert.False(File.Exists(database.FilePath), "Registering and creating the database opens nothing.");
        var service = Assert.IsType<SqliteDatabaseService>(Assert.Single(provider.GetServices<IHostedService>()));

        var maintenance = provider.GetRequiredService<KitMaintenance>();
        Assert.Equal(
            [KitSqliteServiceCollectionExtensions.IntegrityCheckStep, KitSqliteServiceCollectionExtensions.SnapshotsStep, KitSqliteServiceCollectionExtensions.CheckpointStep],
            maintenance.Steps.Select(s => s.Name));
        var run = await maintenance.RunAsync(null, Ct);
        Assert.NotNull(run);
        Assert.True(File.Exists(database.FilePath));

        // The checkpoint last: the backstop of the ones asked for since the last run.
        var steps = Assert.IsAssignableFrom<IEnumerable<IDictionary<string, object?>>>(run["Steps"]).ToList();
        var checkpoint = Assert.IsAssignableFrom<IDictionary<string, object?>>(steps[^1]["Result"]);
        Assert.Equal((KitSqliteServiceCollectionExtensions.CheckpointStep, true, false), ((string?)steps[^1]["Name"], (bool)checkpoint["Complete"]!, (bool)checkpoint["Pending"]!));
        Assert.Single(database.ListSnapshots(), s => s.Kind == Jellyfin.Plugin.Kit.Storage.SnapshotKind.Daily);
        Assert.True(database.LastQuickCheck!.Ok);

        var diagnostics = provider.GetRequiredService<IKitDiagnostics>().Describe();
        var store = Assert.IsAssignableFrom<IDictionary<string, object?>>(diagnostics["Store"]);
        Assert.Equal("SQLite", store["Engine"]);
        Assert.Equal("Ok", store["Status"]);
        Assert.Equal(1, store["DailySnapshots"]);

        // The hosted service lets go of the file when the server stops (as measured in the server: only <db>.db is left).
        SqliteFixture.AddFamilies(database, 2);
        Assert.Equal("locked", SqliteFiles.TryOpenExclusive(database.FilePath));
        await service.StopAsync(Ct);
        Assert.Equal("free", SqliteFiles.TryOpenExclusive(database.FilePath));
        Assert.Empty(SqliteFiles.ListSidecars(database.FilePath));
    }

    [Fact]
    public async Task TheHostedService_OpensTheDatabase_OnceTheServerHasStarted()
    {
        var logger = new ListLogger();
        var host = new Mock<IServerApplicationHost>();
        host.SetupGet(h => h.CoreStartupHasCompleted).Returns(true);
        var database = _fx.Open();
        using var service = new SqliteDatabaseService(host.Object, database, logger);

        await service.StartAsync(Ct);
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.True(File.Exists(_fx.File));
        Assert.Single(logger.At(LogLevel.Information), e => e.Message.Contains("schema 2, Ok", StringComparison.Ordinal));
        await service.StopAsync(Ct);
    }

    [Fact]
    public async Task ADatabaseThatCantBeOpened_IsUnavailable_NothingIsMoved_AndTheNextUseTriesAgain()
    {
        var database = _fx.Open();
        SqliteFixture.AddFamilies(database, 2);
        database.Dispose();
        var blocked = _fx.Open();

        using (new FileStream(_fx.File, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal(SqliteDatabaseStatus.Unavailable, blocked.Open());
            Assert.True(blocked.IsReadOnly);
            Assert.Equal(SqliteMessages.Unavailable, blocked.StatusMessage);
            var read = Assert.Throws<PluginException>(() => SqliteFixture.Count(blocked, "Parents"));
            Assert.Equal((PluginErrorKind.Unavailable, SqliteMessages.Unavailable), (read.Kind, read.Message));
            Assert.Null(await blocked.SnapshotAsync(7, Ct));
            var described = blocked.Describe();
            Assert.Equal("Unavailable", described["Status"]);
            Assert.Contains("SqliteException", (string)described["Error"]!, StringComparison.Ordinal);
            Assert.Empty(blocked.ListQuarantines());
        }

        Assert.Single(_fx.Logger.At(LogLevel.Error), e => e.Message.Contains("Could not open the database", StringComparison.Ordinal));

        // Uses within OpenRetryInterval answer at once; the next one after it opens the database.
        Assert.Equal(SqliteDatabaseStatus.Unavailable, blocked.Open());
        _fx.Clock.Advance(SqliteDatabase.OpenRetryInterval);
        Assert.Equal(SqliteDatabaseStatus.Ok, blocked.Open());
        Assert.Equal(2, SqliteFixture.Count(blocked, "Parents"));
        Assert.Contains(_fx.Logger.At(LogLevel.Information), e => e.Message.Contains("after an earlier failure", StringComparison.Ordinal));
    }

    [Fact]
    public void Describe_ListsTheStatusAndTheKeptFiles_WithoutThrowing()
    {
        var database = _fx.Open();
        SqliteFixture.AddFamilies(database, 1);

        var described = database.Describe();

        Assert.Equal(("SQLite", "userratings", 2, 2, "Ok", false), (described["Engine"], described["Name"], described["Version"], described["CurrentVersion"], described["Status"], described["ReadOnly"]));
        Assert.Null(described["Message"]);
        Assert.Equal(0, described["QuarantineCount"]);
        Assert.Null(described["Recovery"]);
        Assert.Equal(1L, described["Generation"]);
    }

    private static bool IsSqlite(Type type)
        => type.Namespace is { } ns && (ns.StartsWith("Microsoft.Data.Sqlite", StringComparison.Ordinal) || ns.StartsWith("SQLitePCL", StringComparison.Ordinal));

    private static bool IsNoInlining(MethodBase method) => (method.MethodImplementationFlags & MethodImplAttributes.NoInlining) != 0;

    private static bool SignatureNamesSqlite(MethodBase method)
        => method.GetParameters().Select(p => p.ParameterType).Concat(method is MethodInfo info ? [info.ReturnType] : []).SelectMany(MethodBodyReferences.Expand).Any(IsSqlite);

    private static bool IsGenerated(MethodBase method) => method.Name.StartsWith('<') || method.DeclaringType!.Name.StartsWith('<');

    // The member a compiler-generated method belongs to: "<SnapshotAsync>d__85".MoveNext, "<ExportData>b__0", ...
    private static string Owner(MethodBase method)
    {
        if (!IsGenerated(method))
        {
            return method.Name;
        }

        var match = OwnerPattern().Match(method.Name.StartsWith('<') ? method.Name : method.DeclaringType!.Name);
        return match.Success ? match.Groups[1].Value : method.Name;
    }

    [GeneratedRegex("^<([^>]+)>", RegexOptions.CultureInvariant)]
    private static partial Regex OwnerPattern();

    private ServiceProvider Provider(Action<IServiceCollection> register)
    {
        var services = new ServiceCollection();
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(p => p.DataPath).Returns(_fx.Directory.Path);
        var host = new Mock<IServerApplicationHost>();
        host.SetupGet(h => h.CoreStartupHasCompleted).Returns(true);
        services.AddSingleton(paths.Object);
        services.AddSingleton(host.Object);
        services.AddLogging();
        services.AddKit(WebKit.Options());
        services.AddKitDiagnostics();
        services.AddSingleton(new KitAbiStatus("Kit Test", "10.11", "10.11.5", isActive: true, "ok"));
        services.AddKitMaintenance();
        register(services);
        var provider = services.BuildServiceProvider();
        return provider;
    }
}
