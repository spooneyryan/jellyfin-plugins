using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Data.Events.Users;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Kit.Diagnostics;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.Tests.TestHosting;
using Jellyfin.Plugin.Kit.Users;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Kit.Tests.Users;

/// <summary>
/// <c>AddKitUsers</c> registers the consumer Jellyfin's <c>EventManager</c> resolves from a new scope for
/// <see cref="UserDeletedEventArgs"/>, which applies the plugin's <see cref="KitOptions.DeletedUserPolicy"/> to every
/// owner, the reconciler with its marks in the plugin's data folder, and the <c>Users</c> entry of Diagnostics.
/// </summary>
public sealed class KitUserDeletedConsumerTests : IDisposable
{
    private readonly TempDirectory _data = new();

    public void Dispose() => _data.Dispose();

    [Fact]
    public async Task ADeletedUser_GetsThePluginsPolicy_ThroughTheEventConsumer()
    {
        var users = KitUsersTests.Users("admin", "viewer");
        var viewer = users[1];
        var owner = new FakeUserDataOwner().Add(users[0].Id, 1).Add(viewer.Id, 2, hasFile: true, name: "viewer");
        using var provider = Build(owner, users[..1], DeletedUserPolicy.Anonymize).BuildServiceProvider();

        // As Jellyfin's EventManager does it (v10.11.5 Events/EventManager.cs:43-63): a new scope, every consumer.
        using (var scope = provider.CreateScope())
        {
            var consumer = Assert.Single(scope.ServiceProvider.GetServices<IEventConsumer<UserDeletedEventArgs>>());
            Assert.IsType<KitUserDeletedConsumer>(consumer);
            await consumer.OnEvent(new UserDeletedEventArgs(viewer));
        }

        Assert.Equal([(viewer.Id, DeletedUserPolicy.Anonymize)], owner.Deletions);
        Assert.All(owner.Records.Where(r => r.UserId == viewer.Id), r => Assert.True(r.Tombstone));
        Assert.True(provider.GetRequiredService<UserReconciler>().IsOrphan(viewer.Id));
        Assert.True(File.Exists(Path.Combine(_data.Path, "data", "kittest", KitUsersDocument.FileName)));
    }

    [Fact]
    public void AddKitUsers_RegistersOnce_WithTheUsersDiagnosticsEntry()
    {
        var owner = new FakeUserDataOwner();
        var services = Build(owner, KitUsersTests.Users("admin"), DeletedUserPolicy.Delete);
        services.AddKitUsers();

        Assert.Single(services, d => d.ServiceType == typeof(UserReconciler));
        Assert.Single(services, d => d.ServiceType == typeof(KitUserMarks));
        var consumer = Assert.Single(services, d => d.ServiceType == typeof(IEventConsumer<UserDeletedEventArgs>));
        Assert.Equal(ServiceLifetime.Scoped, consumer.Lifetime);

        using var provider = services.BuildServiceProvider();
        var source = Assert.Single(provider.GetServices<IKitDiagnosticsSource>(), s => s.Key == "Users");
        var described = Assert.IsAssignableFrom<IDictionary<string, object?>>(source.Describe());
        Assert.Equal("Delete", described["DeletedUserPolicy"]);
        Assert.Equal(1, described["ServerUsers"]);
    }

    [Fact]
    public async Task AnOwnerThatCantBeCreated_CostsOnlyItself_IsSkippedLikeAReadOnlyOne_AndIsTriedAgain()
    {
        // The reconciler used to take its owners from one GetServices call when it was created, so one owner that
        // couldn't be created left the plugin without the deleted-user policy and the reconciler.
        var users = KitUsersTests.Users("admin", "viewer");
        var viewer = users[1];
        var owner = new FakeUserDataOwner().Add(viewer.Id, 2, hasFile: true, name: "viewer");
        var late = new FakeUserDataOwner("late").Add(viewer.Id, 1);
        var fails = true;
        var services = Build(owner, users[..1], DeletedUserPolicy.KeepAsOrphan)
            .AddSingleton(new KitComponent<IUserDataOwner>("Late owner", _ => fails ? throw new InvalidOperationException("no data folder") : late));
        services.AddKitUserDataOwner<FakeUserDataOwner>();
        Assert.Equal(2, services.Count(d => d.ServiceType == typeof(KitComponent<IUserDataOwner>)));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IUserDataOwner));
        using var provider = services.BuildServiceProvider();
        var reconciler = provider.GetRequiredService<UserReconciler>();

        using (var scope = provider.CreateScope())
        {
            await Assert.Single(scope.ServiceProvider.GetServices<IEventConsumer<UserDeletedEventArgs>>()).OnEvent(new UserDeletedEventArgs(viewer));
        }

        Assert.Equal([(viewer.Id, DeletedUserPolicy.KeepAsOrphan)], owner.Deletions);
        Assert.Empty(late.Deletions);
        var described = (IDictionary<string, object?>)Assert.Single(provider.GetServices<IKitDiagnosticsSource>(), s => s.Key == "Users").Describe()!;
        Assert.Equal("no data folder", ((IEnumerable<IDictionary<string, object?>>)described["Unavailable"]!).Single()["Problem"]);
        Assert.Equal(["Late owner"], (IEnumerable<string>)described["SkippedOwners"]!);

        // The admin actions refuse while it is missing (its records would be left out); a dry run still answers.
        Assert.Equal(Jellyfin.Plugin.Kit.Api.PluginErrorKind.Conflict, (await Assert.ThrowsAsync<Jellyfin.Plugin.Kit.Api.PluginException>(() => reconciler.RemapByNameAsync(dryRun: false, TestContext.Current.CancellationToken))).Kind);
        await reconciler.RemapByNameAsync(dryRun: true, TestContext.Current.CancellationToken);

        // Created at its next use: counted again, and its records get the policy an admin applies.
        fails = false;
        var run = await reconciler.ReconcileAsync(TestContext.Current.CancellationToken);
        Assert.Empty(run.SkippedOwners);
        Assert.Equal(3, run.Records);
        await reconciler.ApplyPolicyAsync([viewer.Id], TestContext.Current.CancellationToken);
        Assert.Equal([(viewer.Id, DeletedUserPolicy.KeepAsOrphan)], late.Deletions);
    }

    private IServiceCollection Build(FakeUserDataOwner owner, IReadOnlyList<User> serverUsers, DeletedUserPolicy policy)
    {
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(p => p.DataPath).Returns(Path.Combine(_data.Path, "data"));
        var userManager = new Mock<IUserManager>();
        userManager.SetupGet(m => m.Users).Returns(serverUsers);
        var source = TestKit.Options;
        var options = new KitOptions
        {
            PluginName = source.PluginName,
            RoutePrefix = source.RoutePrefix,
            Prefix = source.Prefix,
            SchemaPrefix = source.SchemaPrefix,
            DataFolder = source.DataFolder,
            PluginAssembly = source.PluginAssembly,
            PluginId = source.PluginId,
            DeletedUserPolicy = _ => policy
        };

        var services = new ServiceCollection()
            .AddSingleton(paths.Object)
            .AddSingleton(userManager.Object)
            .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
            .AddSingleton(owner);
        services.AddKit(options).AddKitUsers().AddKitUserDataOwner<FakeUserDataOwner>();
        return services;
    }
}
