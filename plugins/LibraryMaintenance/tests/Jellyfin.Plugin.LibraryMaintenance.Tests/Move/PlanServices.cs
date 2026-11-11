using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.LibraryMaintenance.Configuration;
using Jellyfin.Plugin.LibraryMaintenance.Move;
using Jellyfin.Plugin.LibraryMaintenance.Move.Paths;
using Jellyfin.Plugin.LibraryMaintenance.Move.Preflight;
using Jellyfin.Plugin.LibraryMaintenance.Operations;
using Jellyfin.Plugin.LibraryMaintenance.Tests.Move.Planning;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Jellyfin.Plugin.LibraryMaintenance.Tests.Move;

/// <summary>
/// Move's planning services over a <see cref="PlannerWorld"/>: sessions, users and their data as mocks, a clock a test
/// moves, and the settings a test changes in place. <see cref="Register"/> puts the same world into a web host.
/// </summary>
internal sealed class PlanServices : IDisposable
{
    public PlanServices(PlannerWorld world)
    {
        World = world;
        SessionManager.SetupGet(s => s.Sessions).Returns(Sessions);
        Users.SetupGet(u => u.Users).Returns(() => AllUsers);
        Users.Setup(u => u.GetUserById(It.IsAny<Guid>())).Returns((Guid id) => AllUsers.Find(u => u.Id == id));
        UserData.Setup(d => d.GetUserData(It.IsAny<User>(), It.IsAny<BaseItem>()))
            .Returns((User user, BaseItem item) => Data.TryGetValue((user.Id, item.Id), out var data) ? data : new UserItemData { Key = item.Id.ToString("N") });
        FileSystem.Setup(f => f.GetValidFilename(It.IsAny<string>())).Returns((string name) => PlannerWorld.ValidFilename(name));
        Feature = new MoveFeature(new TestConfiguration(Configuration), () => true);
        Gate = new LibraryOperationGate(world.Library.Object);
        Eligibility = new MoveEligibility(world.Access, world.Catalog, Gate, Feature, static _ => false);
        Preflight = new MovePreflight(SessionManager.Object, Gate, new FileLockProbe(static (_, _) => Task.CompletedTask), new FolderWriteAccess(static _ => false));
        Destinations = new MoveDestinations(world.Catalog, Resolver, world.Volumes, world.Library.Object, world.Planner, Feature);
        Preserved = new MovePreservedSummary(world.Library.Object, Users.Object, UserData.Object, Feature);
        Plans = new MovePlanService(Eligibility, world.Catalog, Resolver, world.Planner, Preflight, Destinations, Preserved, world.Volumes, Feature, Clock);
        AllUsers.Add(world.Bob);
    }

    public PlannerWorld World { get; }

    public PluginConfiguration Configuration { get; } = new();

    public ManualClock Clock { get; } = new(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));

    public List<SessionInfo> Sessions { get; } = [];

    public List<User> AllUsers { get; } = [];

    public Dictionary<(Guid User, Guid Item), UserItemData> Data { get; } = [];

    public Mock<ISessionManager> SessionManager { get; } = new();

    public Mock<IUserManager> Users { get; } = new();

    public Mock<IUserDataManager> UserData { get; } = new();

    public Mock<IFileSystem> FileSystem { get; } = new();

    public DestinationResolver Resolver { get; } = new(static _ => false);

    public MoveFeature Feature { get; }

    public LibraryOperationGate Gate { get; }

    public MoveEligibility Eligibility { get; }

    public MovePreflight Preflight { get; }

    public MoveDestinations Destinations { get; }

    public MovePreservedSummary Preserved { get; }

    public MovePlanService Plans { get; }

    public User AddUser(string name)
    {
        var user = new User(name, "DefaultAuthenticationProvider", "DefaultPasswordResetProvider") { Id = Guid.NewGuid() };
        AllUsers.Add(user);
        return user;
    }

    // The world's services in a web host, in place of the registrator's.
    public void Register(IServiceCollection services)
    {
        services.AddSingleton(World.Library.Object);
        services.AddSingleton(Users.Object);
        services.AddSingleton(UserData.Object);
        services.AddSingleton(SessionManager.Object);
        services.AddSingleton(FileSystem.Object);
        services.AddSingleton(World.Access);
        services.AddSingleton(World.Catalog);
        services.AddSingleton<IVolumeInfoProvider>(World.Volumes);
        services.AddSingleton(World.Planner);
        services.AddSingleton(Resolver);
        services.AddSingleton(Feature);
        services.AddSingleton(Gate);
        services.AddSingleton(Eligibility);
        services.AddSingleton(Preflight);
        services.AddSingleton(Destinations);
        services.AddSingleton(Preserved);
        services.AddSingleton(Plans);
    }

    public void Dispose() => Gate.Dispose();

    /// <summary>A clock a test sets.</summary>
    internal sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
