using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.AccessRoles.Access;
using Jellyfin.Plugin.AccessRoles.Audit;
using Jellyfin.Plugin.AccessRoles.Configuration;
using Jellyfin.Plugin.AccessRoles.Enforcement;
using Jellyfin.Plugin.AccessRoles.Hosting;
using Jellyfin.Plugin.AccessRoles.Policy;
using Jellyfin.Plugin.AccessRoles.Rules;
using Jellyfin.Plugin.AccessRoles.Services;
using Jellyfin.Plugin.AccessRoles.Storage;
using Jellyfin.Plugin.AccessRoles.Sync;
using Jellyfin.Plugin.Kit.Diagnostics;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.Logging;
using Jellyfin.Plugin.Kit.Users;
using Jellyfin.Plugin.Kit.Web;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AccessRoles.Tests;

/// <summary>
/// Access Roles' registrator: the kit's ABI guard first, then the kit, the
/// store, the manifest, Diagnostics, the roles API, the user-data owner and the daily maintenance; nothing that enforces yet.
/// </summary>
public sealed class PluginServiceRegistratorTests : IDisposable
{
    private readonly TempDirectory _directory = new();

    [Theory]
    [InlineData("10.11.5")]
    [InlineData("10.11.11")]
    public void On1011_RegistersTheKit_TheStore_TheManifest_Diagnostics_AndTheSnapshot(string version)
    {
        var services = Register(version);

        Assert.True(Assert.IsType<KitAbiStatus>(Assert.Single(services, d => d.ServiceType == typeof(KitAbiStatus)).ImplementationInstance).IsActive);
        Assert.Same(AccessRolesKit.Options, Assert.Single(services, d => d.ServiceType == typeof(KitOptions)).ImplementationInstance);
        Assert.Single(services, d => d.ServiceType == typeof(KitPaths));
        Assert.Single(services, d => d.ServiceType == typeof(AccessRolesPaths));
        Assert.Equal(ServiceLifetime.Singleton, Assert.Single(services, d => d.ServiceType == typeof(AccessRolesStore)).Lifetime);
        Assert.Equal(ServiceLifetime.Singleton, Assert.Single(services, d => d.ServiceType == typeof(ActionManifest)).Lifetime);
        Assert.Single(services, d => d.ServiceType == typeof(IPluginConfigurationProvider));

        // The web client through the kit: the index.html middleware, the only startup filter, and the script.
        Assert.Equal(typeof(IndexHtmlInjectionStartupFilter), Assert.Single(services, d => d.ServiceType == typeof(IStartupFilter)).ImplementationType);
        Assert.Single(services, d => d.ServiceType == typeof(IEmbeddedScript));

        // Diagnostics and its entries, the audit log's and the kit's Users among them.
        Assert.Single(services, d => d.ServiceType == typeof(IKitDiagnostics));
        Assert.Equal(
            ["ActionManifest", "Audit", "Enforcement", "LibrarySync", "Maintenance", "Records", "RuleTable", "ServerUsers", "Store", "Users"],
            services.Where(d => d.ServiceType == typeof(IKitDiagnosticsSource)).Select(d => DescribeKey(d)).Order(StringComparer.Ordinal));

        // The roles API: the catalog, the server's users, the audit log, the service behind both public interfaces, and
        // the store as the kit's user-data owner.
        Assert.Single(services, d => d.ServiceType == typeof(ILibraryCatalogSource));
        Assert.Single(services, d => d.ServiceType == typeof(IServerUsers));
        Assert.Single(services, d => d.ServiceType == typeof(AccessRolesAuditLog));
        Assert.Single(services, d => d.ServiceType == typeof(AccessRolesAdminService));
        Assert.Single(services, d => d.ServiceType == typeof(IAccessRolesAdmin));
        Assert.Single(services, d => d.ServiceType == typeof(IAccessRolesAudit));
        Assert.Single(services, d => d.ServiceType == typeof(AccessRolesUserDataOwner));
        Assert.Equal(
            [typeof(AccessRolesUserDataOwner).FullName],
            services.Where(d => d.ImplementationInstance is KitComponent<IUserDataOwner>).Select(d => ((KitComponent<IUserDataOwner>)d.ImplementationInstance!).Name));

        // The library sync: the only policy writer, its locks, the sync, its status and the library structure filter.
        Assert.Single(services, d => d.ServiceType == typeof(UserPolicyLocks));
        Assert.Single(services, d => d.ServiceType == typeof(PolicyWriter));
        Assert.Single(services, d => d.ServiceType == typeof(LibrarySync));
        Assert.Single(services, d => d.ServiceType == typeof(LibrarySyncStatus));
        Assert.Single(services, d => d.ServiceType == typeof(LibrarySyncService));
        Assert.Single(services, d => d.ServiceType == typeof(LibraryStructureFilter));

        // The optional native mirror, which the library sync runs after each real run.
        Assert.Single(services, d => d.ServiceType == typeof(PreferenceMirrorSync));

        // The settings enforcement: the rule table, the lock snapshots, the counter, the filter, /Me.
        Assert.Single(services, d => d.ServiceType == typeof(ActionRuleTable));
        Assert.Single(services, d => d.ServiceType == typeof(EffectiveRulesCache));
        Assert.Single(services, d => d.ServiceType == typeof(BlockCounter));
        Assert.Single(services, d => d.ServiceType == typeof(EnforcementStatus));
        Assert.Single(services, d => d.ServiceType == typeof(AccessRulesActionFilter));
        Assert.Single(services, d => d.ServiceType == typeof(IAccessRolesMe));

        // The daily maintenance with its snapshot, user and audit retention steps, the start-up service and the library sync;
        // no other hosted service.
        Assert.Single(services, d => d.ServiceType == typeof(KitMaintenance));
        Assert.Equal(
            ["Audit", "Snapshots", "Users"],
            services.Where(d => d.ServiceType == typeof(KitComponent<IKitMaintenanceStep>)).Select(d => ((KitComponent<IKitMaintenanceStep>)d.ImplementationInstance!).Name).Order(StringComparer.Ordinal));
        Assert.Equal(
            [typeof(BlockCounterService).FullName, typeof(RuleTableSelfCheck).FullName, typeof(AccessRolesStartup).FullName, typeof(LibrarySyncService).FullName],
            services.Where(d => d.ServiceType == typeof(IHostedService)).Select(d => (d.ImplementationType ?? d.ImplementationFactory?.Method.ReturnType)?.FullName).Order(StringComparer.Ordinal));

        // One MVC option since 0.9.5: Access Roles' one global filter at −10000, which runs the library structure hook
        // (a filter of its own since 0.9.3) and the user policy hooks (since 0.9.4); the event consumers: the kit's of deleted users (whose
        // assignment and ledger entry go), and since 0.9.4 the user-created (default roles) and user-updated (12.1+)
        // ones, built by factories.
        Assert.Equal(1, services.Count(d => d.ServiceType == typeof(IConfigureOptions<MvcOptions>)));
        Assert.Equal(
            [typeof(UserCreatedConsumer).FullName, typeof(UserUpdatedConsumer).FullName, typeof(KitUserDeletedConsumer).FullName],
            services.Where(d => d.ServiceType.IsGenericType && d.ServiceType.GetGenericTypeDefinition() == typeof(MediaBrowser.Controller.Events.IEventConsumer<>))
                .Select(d => (d.ImplementationType ?? d.ImplementationFactory?.Method.ReturnType)?.FullName).Order(StringComparer.Ordinal));
    }

    // The emergency stop at the server's start (the SAFE-MODE file or EnforcementEnabled false): only the kit's paths, the embedded scripts
    // (the admin page) and the stop; no store, filter, sync, hosted service, startup filter, MVC option or event consumer.
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void WithTheEmergencyStopAtTheStart_RegistersNothingButTheAdminPage(bool safeMode, bool disabled)
    {
        if (safeMode)
        {
            Directory.CreateDirectory(Path.Combine(_directory.Path, "accessroles"));
            File.WriteAllText(Path.Combine(_directory.Path, "accessroles", "SAFE-MODE"), string.Empty);
        }

        if (disabled)
        {
            WriteConfiguration("<PluginConfiguration><AuditRetentionMonths>12</AuditRetentionMonths><EnforcementEnabled> False </EnforcementEnabled></PluginConfiguration>");
        }

        var services = Register("10.11.5", ServerPaths());

        var inert = Assert.IsType<AccessRolesInert>(Assert.Single(services, d => d.ServiceType == typeof(AccessRolesInert)).ImplementationInstance);
        Assert.Equal((safeMode, disabled), (inert.SafeMode, inert.EnforcementDisabled));
        Assert.Equal(Path.Combine(_directory.Path, "accessroles", "SAFE-MODE"), inert.SafeModeFile);
        Assert.Equal(Path.Combine(_directory.Path, "plugins", "configurations", "Jellyfin.Plugin.AccessRoles.xml"), inert.ConfigurationFile);
        Assert.Equal(
            new[] { typeof(AccessRolesInert), typeof(EmbeddedScript), typeof(IEmbeddedScript), typeof(KitAbiStatus), typeof(KitOptions), typeof(KitPaths), typeof(IApplicationPaths) }.Select(t => t.FullName).Order(StringComparer.Ordinal),
            services.Select(d => d.ServiceType.FullName).Order(StringComparer.Ordinal));
        using var provider = services.AddLogging().BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<IEmbeddedScript>());
    }

    // The negative control: no file, EnforcementEnabled true, an element elsewhere, an unreadable file or a SAFE-MODE folder
    // stop nothing (fail closed: the plugin runs and enforces).
    [Theory]
    [InlineData(null)]
    [InlineData("<PluginConfiguration><EnforcementEnabled>true</EnforcementEnabled></PluginConfiguration>")]
    [InlineData("<PluginConfiguration><Other><EnforcementEnabled>false</EnforcementEnabled></Other></PluginConfiguration>")]
    [InlineData("<PluginConfiguration><EnforcementEnabled>false</EnforcementEnabled>")]
    [InlineData("folder")]
    public void WithoutTheEmergencyStop_RegistersEverything(string? configuration)
    {
        if (configuration == "folder")
        {
            Directory.CreateDirectory(Path.Combine(_directory.Path, "accessroles", "SAFE-MODE"));
        }
        else if (configuration is not null)
        {
            WriteConfiguration(configuration);
        }

        var services = Register("10.11.5", ServerPaths());

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(AccessRolesInert));
        Assert.Single(services, d => d.ServiceType == typeof(AccessRolesStore));
        Assert.Single(services, d => d.ServiceType == typeof(AccessRulesActionFilter));
        Assert.Single(services, d => d.ServiceType == typeof(IStartupFilter));
        Assert.Null(AccessRolesInert.Detect(services));
    }

    [Fact]
    public void TheStop_IsReadFromTheServersPathsInstance_AndNeverThrows()
    {
        var throwing = new Mock<IApplicationPaths>();
        throwing.SetupGet(p => p.DataPath).Throws<IOException>();
        File.WriteAllText(Path.Combine(_directory.Path, "SAFE-MODE"), string.Empty);

        Assert.Null(AccessRolesInert.Detect(new ServiceCollection()));
        Assert.Null(AccessRolesInert.Detect(new ServiceCollection().AddSingleton(throwing.Object)));
        Assert.Null(AccessRolesInert.Detect(new ServiceCollection().AddSingleton(_ => ServerPaths())));
        Assert.Null(AccessRolesInert.Detect(null, null));
        Assert.Null(AccessRolesInert.Detect(_directory.Path, _directory.Path));
    }

    [Fact]
    public void On121_RegistersNothingButTheAbiStatus()
    {
        var services = Register("12.1.0");

        var status = Assert.IsType<KitAbiStatus>(Assert.Single(services).ImplementationInstance);
        Assert.False(status.IsActive);
        Assert.Equal("Access Roles is built for Jellyfin 10.11, but the server is Jellyfin 12.1.0: the plugin is inactive. Install the Access Roles build for Jellyfin 12.", status.Message);
    }

    [Fact]
    public void WhenTheVersionCheckThrows_RegistersNothingButTheAbiStatus()
    {
        var host = new Mock<IServerApplicationHost>();
        host.SetupGet(h => h.ApplicationVersion).Throws<MissingMethodException>();
        var services = new ServiceCollection();

        new PluginServiceRegistrator().RegisterServices(services, host.Object);

        Assert.False(Assert.IsType<KitAbiStatus>(Assert.Single(services).ImplementationInstance).IsActive);
    }

    [Fact]
    public void TheStore_IsAccessRolesJson_InTheDataFolder_WithTheKitLoggerCategory_AndReadingCreatesNothing()
    {
        var loggers = new Mock<ILoggerFactory>();
        loggers.Setup(f => f.CreateLogger(It.IsAny<string>())).Returns(NullLogger.Instance);
        using var provider = Build(loggers.Object);

        var store = provider.GetRequiredService<AccessRolesStore>();

        Assert.Equal(Path.Combine(_directory.Path, "accessroles", "accessroles.json"), store.Documents.FilePath);
        loggers.Verify(f => f.CreateLogger("Jellyfin.Plugin.AccessRoles.Kit.Storage"), Times.Once);
        Assert.Empty(store.Current.Assignments);
        Assert.False(store.IsReadOnly);
        Assert.False(Directory.Exists(Path.Combine(_directory.Path, "accessroles")), "Reading created the data folder.");
    }

    [Fact]
    public async Task TheStartup_CreatesTheDataFolderAndFile_AfterTheStartupGate()
    {
        var logger = new ListLogger();
        var host = new Mock<IServerApplicationHost>();
        host.SetupGet(h => h.CoreStartupHasCompleted).Returns(true);
        using var store = AccessRolesStore.Create(Path.Combine(_directory.Path, "accessroles", "accessroles.json"), NullLogger.Instance);
        using var startup = new AccessRolesStartup(host.Object, store, logger);

        await startup.StartAsync(TestContext.Current.CancellationToken);
        await WaitForAsync(() => File.Exists(store.Documents.FilePath));
        await startup.StopAsync(TestContext.Current.CancellationToken);

        Assert.Contains(logger.At(LogLevel.Information), e => e.Message.Contains("created its data file", StringComparison.Ordinal));
        Assert.Empty(logger.At(LogLevel.Warning));
        Assert.Equal(0, startup.Failures);
    }

    // The mirror's records in the file the store moved aside (its .bak used instead, which lacks
    // them) are taken back into the store at the start, so the mirror, Release all locks and the uninstall find them.
    [Fact]
    public async Task TheStartup_TakesBackTheMirrorRecordsOfACopyTheStoreCouldntUse()
    {
        var file = Path.Combine(_directory.Path, "accessroles", "accessroles.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var kid = Guid.Parse("11111111111111111111111111111111");
        await File.WriteAllTextAsync(file + ".bak", """{"Version":1}""", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(file, "{\"Version\":1,\"Roles\":\"corrupt\",\"Assignments\":[],\"PreferenceMirror\":{\"" + kid.ToString("N") + "\":{}}}", TestContext.Current.CancellationToken);
        var logger = new ListLogger();
        var host = new Mock<IServerApplicationHost>();
        host.SetupGet(h => h.CoreStartupHasCompleted).Returns(true);
        using var store = AccessRolesStore.Create(file, NullLogger.Instance);
        using var startup = new AccessRolesStartup(host.Object, store, logger);

        await startup.StartAsync(TestContext.Current.CancellationToken);
        await WaitForAsync(() => store.Current.PreferenceMirror.ContainsKey(kid));
        await startup.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(Jellyfin.Plugin.Kit.Storage.StoreStatus.RecoveredFromBackup, store.Status);
        Assert.Null(store.Degraded.Current);
        Assert.Empty(store.Degraded.MirrorEvidence);
        Assert.False(File.Exists(store.Degraded.MarkerPath));
        Assert.Contains(logger.At(LogLevel.Warning), e => e.Message.Contains("took back the records of 1 users", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheStartup_WarnsAboutAStoreThatIsntOk_AndWritesNothing()
    {
        var file = Path.Combine(_directory.Path, "accessroles", "accessroles.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, """{"Version":2}""", TestContext.Current.CancellationToken);
        var logger = new ListLogger();
        var host = new Mock<IServerApplicationHost>();
        host.SetupGet(h => h.CoreStartupHasCompleted).Returns(true);
        using var store = AccessRolesStore.Create(file, NullLogger.Instance);
        using var startup = new AccessRolesStartup(host.Object, store, logger);

        await startup.StartAsync(TestContext.Current.CancellationToken);
        await WaitForAsync(() => logger.At(LogLevel.Warning).Any());
        await startup.StopAsync(TestContext.Current.CancellationToken);

        Assert.Contains("ReadOnlyNewerVersion", Assert.Single(logger.At(LogLevel.Warning)).Message, StringComparison.Ordinal);
        Assert.Equal("""{"Version":2}""", await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Diagnostics_DescribeTheStore_TheRecords_Enforcement_TheManifest_AndTheServersUsersThroughKitUsers()
    {
        var users = new List<User> { NewUser("admin", admin: true), NewUser("kid"), NewUser("off", disabled: true) };
        var userManager = new Mock<IUserManager>();
        userManager.SetupGet(m => m.Users).Returns(users);
        using var provider = Build(NullLoggerFactory.Instance, s => s.AddSingleton(userManager.Object));

        var body = provider.GetRequiredService<IKitDiagnostics>().Describe();

        Assert.Equal("10.11", ((Dictionary<string, object?>)body["Abi"]!)["Target"]);
        var store = (IDictionary<string, object?>)body["Store"]!;
        Assert.Equal(("accessroles", 1, "Ok"), (store["Name"], store["CurrentVersion"], store["Status"]));
        Assert.Equal(0, ((IDictionary<string, object?>)body["Records"]!)["Assignments"]);
        var enforcement = (IDictionary<string, object?>)body["Enforcement"]!;
        Assert.Equal((true, "Synced", true, true), (enforcement["Active"], enforcement["LibraryAccess"], enforcement["SettingsLocks"], enforcement["Hardening"]));
        Assert.Empty((IEnumerable<string>)enforcement["NotEnforcedLocks"]!);
        var wide = (IDictionary<string, object?>)enforcement["ServerWide"]!;
        Assert.Equal(("AllowList", true, true, "Standard"), (wide["ConfigurationReads"], wide["BlockNonAdminRestart"], wide["TrimPublicUsers"], wide["LibraryHardening"]));
        Assert.Equal(["encoding"], (IEnumerable<string>)wide["ConfigurationReadAllowList"]!);
        var sync = (IDictionary<string, object?>)body["LibrarySync"]!;
        Assert.Equal(("Active", "Reapply", 5, 0, 0), (sync["State"], sync["DriftPolicy"], sync["ReconcileIntervalMinutes"], sync["PendingReleases"], sync["PendingVerifications"]));
        Assert.Null(sync["LastRun"]);
        var manifest = (IDictionary<string, object?>)body["ActionManifest"]!;
        Assert.Equal(("10.11", "10.11.5", 390, 110), (manifest["JellyfinTarget"], manifest["JellyfinVersion"], manifest["Actions"], manifest["WithRules"]));
        Assert.Null(manifest["Server"]);
        var serverUsers = (IDictionary<string, object?>)body["ServerUsers"]!;
        Assert.Equal((KitUsers.UsersProperty, 3, 1, 1), (serverUsers["ListedThrough"], serverUsers["Users"], serverUsers["Administrators"], serverUsers["Disabled"]));
    }

    [Fact]
    public async Task TheSnapshotStep_WritesADailySnapshot_OfTheStore()
    {
        using var provider = Build(NullLoggerFactory.Instance);
        await provider.GetRequiredService<AccessRolesStore>().CreateFileIfMissingAsync(TestContext.Current.CancellationToken);

        var result = await PluginServiceRegistrator.SnapshotAsync(provider, TestContext.Current.CancellationToken);

        Assert.Equal(7, result!["Keep"]);
        Assert.Null(result["Skipped"]);
        Assert.Single(Directory.GetFiles(Path.Combine(_directory.Path, "accessroles", "snapshots"), "accessroles-*.json"));
    }

    [Fact]
    public async Task TheAuditStep_RotatesTheAuditFolderOfTheDataFolder_WithTheSetting_AndDiagnosticsDescribesIt()
    {
        using var provider = Build(NullLoggerFactory.Instance);
        var audit = provider.GetRequiredService<AccessRolesAuditLog>();
        Directory.CreateDirectory(audit.Directory);
        await File.WriteAllTextAsync(Path.Combine(audit.Directory, "audit-200001.jsonl"), string.Empty, TestContext.Current.CancellationToken);

        var result = await PluginServiceRegistrator.AuditRetentionAsync(provider, TestContext.Current.CancellationToken);
        var described = (IDictionary<string, object?>)provider.GetRequiredService<IKitDiagnostics>().Describe()["Audit"]!;

        Assert.Equal(Path.Combine(_directory.Path, "accessroles", "audit"), audit.Directory);
        Assert.Equal(12, result!["RetentionMonths"]);
        Assert.Equal(["audit-200001.jsonl"], (IEnumerable<string>)result["Deleted"]!);
        Assert.Equal(0, described["Files"]);
        Assert.Equal(0L, described["LastSeq"]);
    }

    public void Dispose() => _directory.Dispose();

    private static ServiceCollection Register(string version, IApplicationPaths? paths = null)
    {
        var host = new Mock<IServerApplicationHost>();
        host.SetupGet(h => h.ApplicationVersion).Returns(Version.Parse(version));
        var services = new ServiceCollection();
        if (paths is not null)
        {
            services.AddSingleton(paths);
        }

        new PluginServiceRegistrator().RegisterServices(services, host.Object);
        return services;
    }

    // The server's paths as 10.11.5's ApplicationHost has them in the collection before the plugins' registrators run.
    private IApplicationPaths ServerPaths()
    {
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(p => p.DataPath).Returns(_directory.Path);
        paths.SetupGet(p => p.PluginConfigurationsPath).Returns(Path.Combine(_directory.Path, "plugins", "configurations"));
        return paths.Object;
    }

    private void WriteConfiguration(string xml)
    {
        var folder = Path.Combine(_directory.Path, "plugins", "configurations");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Jellyfin.Plugin.AccessRoles.xml"), xml);
    }

    private static string DescribeKey(ServiceDescriptor descriptor)
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        return ((IKitDiagnosticsSource)descriptor.ImplementationFactory!(provider)).Key;
    }

    private static User NewUser(string name, bool admin = false, bool disabled = false)
    {
        var user = new User(name, "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider", "Jellyfin.Server.Implementations.Users.DefaultPasswordResetProvider");
        user.SetPermission(PermissionKind.IsAdministrator, admin);
        user.SetPermission(PermissionKind.IsDisabled, disabled);
        return user;
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(20, timeout.Token);
        }
    }

    private ServiceProvider Build(ILoggerFactory loggers, Action<IServiceCollection>? configure = null)
    {
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(p => p.DataPath).Returns(_directory.Path);
        var services = Register("10.11.5");
        services.AddSingleton(paths.Object).AddSingleton(loggers);

        // The settings as a fresh install has them, not the static Plugin.Instance's: other tests create plugin instances
        // (each the new Plugin.Instance) and some change their settings, which tests running beside these would read.
        services.AddSingleton<IPluginConfigurationProvider>(new PluginConfigurationProvider(() => null));
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }
}
