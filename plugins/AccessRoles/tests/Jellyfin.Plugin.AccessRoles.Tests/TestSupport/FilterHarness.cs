using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.AccessRoles.Access;
using Jellyfin.Plugin.AccessRoles.Configuration;
using Jellyfin.Plugin.AccessRoles.Enforcement;
using Jellyfin.Plugin.AccessRoles.Model;
using Jellyfin.Plugin.AccessRoles.Policy;
using Jellyfin.Plugin.AccessRoles.Rules;
using Jellyfin.Plugin.AccessRoles.Storage;
using Jellyfin.Plugin.AccessRoles.Sync;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.TestSupport.Library;
using Jellyfin.Extensions.Json;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ActionConstraints;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AccessRoles.Tests.TestSupport;

/// <summary>
/// Access Roles' global filter over the library sync's real parts (<see cref="SyncHarness"/>): the store, the audit
/// log, the library structure and user policy hooks, and a user manager and display-preferences manager of the test's own that hold each synthetic
/// user's stored configuration and <c>usersettings</c> document, so a merge can be read back; and since 0.9.7 its rules
/// (<see cref="ServerHardening"/>) over the kit's fake library, whose libraries a user may open are the test's
/// (<see cref="VisibleLibraries"/>), with a People-search query of the test's own. Every user is synthetic.
/// </summary>
internal sealed class FilterHarness : IDisposable
{
    private readonly Dictionary<Guid, User> _users = [];
    private readonly Dictionary<Guid, UserConfiguration> _configurations = [];
    private readonly Dictionary<(Guid User, Guid Document, string Client), DisplayPreferences> _documents = [];
    private readonly Dictionary<(Guid User, Guid Document, string Client), Dictionary<string, string?>> _custom = [];
    private readonly LibrarySyncService _service;
    private int _roles;

    public FilterHarness()
    {
        var host = new Mock<IServerApplicationHost>();
        host.SetupGet(h => h.ApplicationVersion).Returns(Version.Parse("10.11.5"));
        Configuration = new FakeConfigurationProvider(() => Sync.Configuration);
        _service = new LibrarySyncService(host.Object, Sync.Sync, Sync.Store, Sync.Writer, null, Configuration, Sync.Status, NullLogger.Instance, Sync.Clock);

        UserManager.Setup(m => m.GetUserById(It.IsAny<Guid>())).Returns((Guid id) => _users.GetValueOrDefault(id));
        UserManager.Setup(m => m.GetUserDto(It.IsAny<User>(), It.IsAny<string?>())).Returns((User user, string? _) => new UserDto { Id = user.Id, Name = user.Username, Configuration = Copy(_configurations[user.Id]) });
        DisplayManager.Setup(m => m.GetDisplayPreferences(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>()))
            .Returns((Guid user, Guid document, string client) => Document(user, document, client));
        DisplayManager.Setup(m => m.ListCustomItemDisplayPreferences(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>()))
            .Returns((Guid user, Guid document, string client) => new Dictionary<string, string?>(Custom(user, document, client)));

        Manifest = ActionManifest.ForBuild();
        Table = new ActionRuleTable(Manifest);
        Cache = new EffectiveRulesCache(Sync.Store);
        UserPolicy = new UserPolicyFilter(Manifest, Sync.Locks, Sync.Sync, _service, new JellyfinServerUsers(Sync.Server.Manager.Object), Sync.Server.Manager.Object, Mock.Of<ISessionManager>(), Sync.Status, NullLogger.Instance, null, TimeSpan.FromSeconds(1));
        Structure = new LibraryStructureFilter(Manifest, _service, Sync.Catalog, NullLogger.Instance);
        Access = new LibraryAccess(Library.Object, user => VisibleLibraries(user), Library.IsVisible, Library.IsVisible);
        Guard = new ConfigurationReadGuard(() => JsonDefaults.Options);
        Hardening = new ServerHardening(UserManager.Object, Access, People, Guard, Counter);
        Filter = Build(UserManager.Object);
    }

    public SyncHarness Sync { get; } = new();

    public FakeConfigurationProvider Configuration { get; }

    public Mock<IUserManager> UserManager { get; } = new(MockBehavior.Loose);

    public Mock<IDisplayPreferencesManager> DisplayManager { get; } = new(MockBehavior.Loose);

    public ActionManifest Manifest { get; }

    public ActionRuleTable Table { get; }

    public EffectiveRulesCache Cache { get; }

    public BlockCounter Counter { get; } = new(null);

    public EnforcementStatus Status { get; } = new();

    public UserPolicyFilter UserPolicy { get; }

    public LibraryStructureFilter Structure { get; }

    public AccessRulesActionFilter Filter { get; private set; }

    /// <summary>Gets the kit's fake library the hardening rules look items up in.</summary>
    public FakeLibrary Library { get; } = new();

    /// <summary>Gets or sets the libraries a user may open (the kit's <c>VisibleLibraries</c>); none by default.</summary>
    public Func<User?, IReadOnlyList<CollectionFolder>> VisibleLibraries { get; set; } = _ => [];

    /// <summary>Gets the People-search query the hardening rule asks.</summary>
    public FakePeople People { get; } = new();

    public LibraryAccess Access { get; }

    public ConfigurationReadGuard Guard { get; }

    public ServerHardening Hardening { get; }

    public HardeningCost Cost { get; } = new();

    /// <summary>A role that locks every lock there is.</summary>
    public static IReadOnlyList<AccessRolesLock> EveryLock => AccessPresets.AllLocks;

    public static ClaimsPrincipal NonAdmin(Guid userId) => new(new ClaimsIdentity([new Claim("Jellyfin-UserId", userId.ToString("N")), new Claim(ClaimTypes.Role, "User")], "Test"));

    public static ClaimsPrincipal Administrator(Guid userId) => new(new ClaimsIdentity([new Claim("Jellyfin-UserId", userId.ToString("N")), new Claim(ClaimTypes.Role, UserPolicyFilter.AdministratorRole)], "Test"));

    // As Jellyfin 10.11.5 builds an API key's principal: the Administrator role, an empty user id, IsApiKey.
    public static ClaimsPrincipal ApiKey() => new(new ClaimsIdentity([new Claim("Jellyfin-UserId", Guid.Empty.ToString("N")), new Claim(ClaimTypes.Role, UserPolicyFilter.AdministratorRole), new Claim("Jellyfin-IsApiKey", "True")], "Test"));

    public static ClaimsPrincipal Anonymous() => new(new ClaimsIdentity());

    /// <summary>
    /// Gets a descriptor of an action: its controller as a type of that full name (in its assembly name), its HTTP methods
    /// and route template, as MVC gives them.
    /// </summary>
    public static ControllerActionDescriptor Descriptor(string controller, string action, string assembly = "Jellyfin.Api", string? method = null, string? template = null, params string[] parameters)
    {
        var descriptor = new ControllerActionDescriptor
        {
            ControllerTypeInfo = new NamedType(controller, assembly),
            ActionName = action,
            ControllerName = controller[(controller.LastIndexOf('.') + 1)..],
            ActionConstraints = method is null ? [] : [new HttpMethodActionConstraint([method])],
            AttributeRouteInfo = template is null ? null : new AttributeRouteInfo { Template = template },
            // A parameter "name=bound" is bound from another name (MVC's BinderModelName), as the manifest writes it.
            Parameters = [.. parameters.Select(p => new ParameterDescriptor
            {
                Name = p.Split('=')[0],
                ParameterType = typeof(Guid?),
                BindingInfo = p.Contains('=', StringComparison.Ordinal) ? new BindingInfo { BinderModelName = p.Split('=')[1] } : null
            })]
        };
        return descriptor;
    }

    /// <summary>
    /// Gets a descriptor of a manifest action with its parameters, as MVC gives them to the rule table (the generic
    /// backstop's scope parameters come from them).
    /// </summary>
    public ControllerActionDescriptor ManifestDescriptor(string controller, string action)
        => Descriptor(controller, action, "Jellyfin.Api", null, null, [.. (Manifest.Find(controller, action) ?? throw new ArgumentException(controller + "." + action)).Parameters]);

    // An action whose controller's assembly can't be read: the rule table's lookup throws on it.
    public static ControllerActionDescriptor Unreadable(string controller, string action) => new()
    {
        ControllerTypeInfo = new UnreadableType(controller),
        ActionName = action,
        ControllerName = controller[(controller.LastIndexOf('.') + 1)..].Replace("Controller", string.Empty, StringComparison.Ordinal),
        ActionConstraints = []
    };

    public User AddUser(string name, UserConfiguration? configuration = null)
    {
        var user = new User(name, "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider", "Jellyfin.Server.Implementations.Users.DefaultPasswordResetProvider");
        _users[user.Id] = user;
        _configurations[user.Id] = configuration ?? Stored();
        return user;
    }

    /// <summary>A stored configuration with a value in every field, unlike the defaults the tests post.</summary>
    public static UserConfiguration Stored() => new()
    {
        AudioLanguagePreference = "fra",
        PlayDefaultAudioTrack = false,
        SubtitleLanguagePreference = "deu",
        DisplayMissingEpisodes = true,
        GroupedFolders = [Guid.Parse("11111111-1111-1111-1111-111111111111")],
        SubtitleMode = SubtitlePlaybackMode.Always,
        DisplayCollectionsView = true,
        EnableLocalPassword = true,
        OrderedViews = [Guid.Parse("22222222-2222-2222-2222-222222222222")],
        LatestItemsExcludes = [Guid.Parse("33333333-3333-3333-3333-333333333333")],
        MyMediaExcludes = [Guid.Parse("44444444-4444-4444-4444-444444444444")],
        HidePlayedInLatest = false,
        RememberAudioSelections = false,
        RememberSubtitleSelections = false,
        EnableNextEpisodeAutoPlay = false,
        CastReceiverId = "stored-receiver"
    };

    /// <summary>A post that differs from <see cref="Stored"/> in every field.</summary>
    public static UserConfiguration Posted() => new()
    {
        AudioLanguagePreference = "eng",
        PlayDefaultAudioTrack = true,
        SubtitleLanguagePreference = "eng",
        DisplayMissingEpisodes = false,
        GroupedFolders = [],
        SubtitleMode = SubtitlePlaybackMode.None,
        DisplayCollectionsView = false,
        EnableLocalPassword = false,
        OrderedViews = [],
        LatestItemsExcludes = [],
        MyMediaExcludes = [],
        HidePlayedInLatest = true,
        RememberAudioSelections = true,
        RememberSubtitleSelections = true,
        EnableNextEpisodeAutoPlay = true,
        CastReceiverId = "posted-receiver"
    };

    public UserConfiguration StoredOf(Guid userId) => _configurations[userId];

    /// <summary>The stored <c>usersettings</c> document of a user (created with defaults on first use, as the core does).</summary>
    public DisplayPreferences Document(Guid user, Guid document, string client)
    {
        if (!_documents.TryGetValue((user, document, client), out var stored))
        {
            stored = new DisplayPreferences(user, document, client);
            _documents[(user, document, client)] = stored;
        }

        return stored;
    }

    public Dictionary<string, string?> Custom(Guid user, Guid document, string client)
    {
        if (!_custom.TryGetValue((user, document, client), out var custom))
        {
            custom = new Dictionary<string, string?>(StringComparer.Ordinal);
            _custom[(user, document, client)] = custom;
        }

        return custom;
    }

    /// <summary>Gives a user a stored <c>usersettings</c> document: two home sections, skip lengths, a theme, custom keys.</summary>
    public void StoreUserSettings(Guid userId, string client = "emby")
    {
        var document = Document(userId, DisplayPreferencesMerger.UserSettingsDocumentId, client);
        document.HomeSections.Clear();
        document.HomeSections.Add(new HomeSection { Order = 0, Type = HomeSectionType.Resume });
        document.HomeSections.Add(new HomeSection { Order = 1, Type = HomeSectionType.LatestMedia });
        document.SkipBackwardLength = 5000;
        document.SkipForwardLength = 15000;
        document.DashboardTheme = "dark";
        document.TvHome = "stored-tvhome";
        var custom = Custom(userId, DisplayPreferencesMerger.UserSettingsDocumentId, client);
        custom["useEpisodeImagesInNextUpAndResume"] = "true";
        custom["landing-11111111111111111111111111111111"] = "movies";
        custom["enableNextVideoInfoOverlay"] = "True";
    }

    /// <summary>Saves a role with these locks (no library mode) and assigns it to the users.</summary>
    public async Task<AccessRole> LockAsync(IEnumerable<AccessRolesLock> locks, params Guid[] users)
    {
        var role = AccessFixtures.Role(100 + ++_roles, "Locks " + _roles, locks);
        await Sync.SaveAsync(d => d with
        {
            Roles = [.. d.Roles, role],
            Assignments = [.. d.Assignments.Where(a => !users.Contains(a.UserId)), .. users.Select(u => new RoleAssignment { UserId = u, RoleIds = [role.Id], State = AccessRolesAssignmentState.Managed, UpdatedUtc = AccessFixtures.Now })]
        });
        return role;
    }

    /// <summary>Removes every role and assignment.</summary>
    public Task UnlockAllAsync() => Sync.SaveAsync(d => d with { Roles = [], Assignments = [] });

    /// <summary>Builds a filter over another user manager (to make a merge throw, say).</summary>
    public AccessRulesActionFilter Build(IUserManager userManager, EffectiveRulesCache? cache = null, UserPolicyFilter? userPolicy = null, ServerHardening? hardening = null, ActionRuleTable? table = null)
        => new(table ?? Table, cache ?? Cache, Configuration, Sync.Paths, userManager, DisplayManager.Object, Counter, Status, userPolicy ?? UserPolicy, Structure, NullLogger.Instance, Sync.Clock, null, hardening ?? Hardening, Cost);

    // A user policy filter over another manifest (one whose own lookup finds no hook).
    public UserPolicyFilter NewUserPolicy(ActionManifest manifest)
        => new(manifest, Sync.Locks, Sync.Sync, _service, new JellyfinServerUsers(Sync.Server.Manager.Object), Sync.Server.Manager.Object, Mock.Of<ISessionManager>(), Sync.Status, NullLogger.Instance, null, TimeSpan.FromSeconds(1));

    /// <summary>
    /// Runs one action through the filter as a caller: the "action" (next) records that it ran and answers 204, or
    /// <paramref name="result"/>, or fails with <paramref name="exception"/> (as MVC hands an action's exception to the filter).
    /// </summary>
    public async Task<FilterRun> RunAsync(ControllerActionDescriptor descriptor, ClaimsPrincipal caller, Dictionary<string, object?>? arguments = null, bool invalidModel = false, AccessRulesActionFilter? filter = null, Func<Task>? inside = null, IActionResult? result = null, Exception? exception = null)
    {
        var http = new DefaultHttpContext { User = caller };
        var context = new ActionExecutingContext(new ActionContext(http, new RouteData(), descriptor), [], arguments ?? [], new object());
        if (invalidModel)
        {
            context.ModelState.AddModelError("body", "The body is invalid.");
        }

        var ran = false;
        await (filter ?? Filter).OnActionExecutionAsync(context, async () =>
        {
            ran = true;
            if (inside is not null)
            {
                await inside();
            }

            return exception is null
                ? new ActionExecutedContext(context, [], new object()) { Result = result ?? new NoContentResult() }
                : new ActionExecutedContext(context, [], new object()) { Exception = exception };
        });
        return new FilterRun(context, ran);
    }

    public Task<FilterRun> RunAsync(string controller, string action, ClaimsPrincipal caller, Dictionary<string, object?>? arguments = null, bool invalidModel = false)
        => RunAsync(Descriptor(controller, action), caller, arguments, invalidModel);

    public void Dispose()
    {
        _service.Dispose();
        Sync.Dispose();
    }

    private static UserConfiguration Copy(UserConfiguration c) => new()
    {
        AudioLanguagePreference = c.AudioLanguagePreference,
        PlayDefaultAudioTrack = c.PlayDefaultAudioTrack,
        SubtitleLanguagePreference = c.SubtitleLanguagePreference,
        DisplayMissingEpisodes = c.DisplayMissingEpisodes,
        GroupedFolders = [.. c.GroupedFolders],
        SubtitleMode = c.SubtitleMode,
        DisplayCollectionsView = c.DisplayCollectionsView,
        EnableLocalPassword = c.EnableLocalPassword,
        OrderedViews = [.. c.OrderedViews],
        LatestItemsExcludes = [.. c.LatestItemsExcludes],
        MyMediaExcludes = [.. c.MyMediaExcludes],
        HidePlayedInLatest = c.HidePlayedInLatest,
        RememberAudioSelections = c.RememberAudioSelections,
        RememberSubtitleSelections = c.RememberSubtitleSelections,
        EnableNextEpisodeAutoPlay = c.EnableNextEpisodeAutoPlay,
        CastReceiverId = c.CastReceiverId
    };

    /// <summary>
    /// Runs one action through the filter, the action answering <paramref name="result"/>: the request's answer is the
    /// filter's own when the action didn't run, else the (possibly rewritten) result.
    /// </summary>
    public async Task<(FilterRun Run, IActionResult? Result)> RunWithResultAsync(ControllerActionDescriptor descriptor, ClaimsPrincipal caller, Dictionary<string, object?>? arguments, IActionResult result, AccessRulesActionFilter? filter = null)
    {
        var http = new DefaultHttpContext { User = caller };
        var context = new ActionExecutingContext(new ActionContext(http, new RouteData(), descriptor), [], arguments ?? [], new object());
        var ran = false;
        ActionExecutedContext? executed = null;
        await (filter ?? Filter).OnActionExecutionAsync(context, () =>
        {
            ran = true;
            executed = new ActionExecutedContext(context, [], new object()) { Result = result };
            return Task.FromResult(executed);
        });
        return (new FilterRun(context, ran), ran ? executed!.Result : context.Result);
    }

    /// <summary>One request through the filter.</summary>
    internal sealed record FilterRun(ActionExecutingContext Context, bool Ran)
    {
        /// <summary>Gets the refusal's body, or <c>null</c> when the request went on.</summary>
        public ProblemDetails? Problem => Context.Result is Jellyfin.Plugin.Kit.Api.KitProblemResult { Value: var problem } ? problem : null;

        /// <summary>Gets a value indicating whether the filter refused with 403 Locked.</summary>
        public bool Locked => !Ran && Problem is { Status: 403, Type: "urn:jellyfin-plugin-kit:locked" };
    }

    /// <summary>
    /// The People-search query of the test's own (<see cref="IPeopleLibraries"/>): the names that have an item in a top
    /// parent, and every question asked.
    /// </summary>
    internal sealed class FakePeople : IPeopleLibraries
    {
        public Dictionary<string, HashSet<Guid>> TopParentsOf { get; } = new(StringComparer.Ordinal);

        public List<(IReadOnlyCollection<string> Names, IReadOnlyCollection<Guid> TopParents)> Asked { get; } = [];

        public Exception? Throw { get; set; }

        public IReadOnlySet<string> NamesWithItemsIn(IReadOnlyCollection<string> names, IReadOnlyCollection<Guid> topParentIds)
        {
            Asked.Add(([.. names], [.. topParentIds]));
            if (Throw is not null)
            {
                throw Throw;
            }

            return names.Where(n => TopParentsOf.TryGetValue(n, out var tops) && tops.Overlaps(topParentIds)).ToHashSet(StringComparer.Ordinal);
        }
    }

    /// <summary>A controller type of any full name and assembly name, as MVC's descriptor carries it.</summary>
    private sealed class NamedType : TypeDelegator
    {
        private readonly string _fullName;
        private readonly AssemblyName _assembly;

        public NamedType(string fullName, string assembly)
            : base(typeof(object))
        {
            _fullName = fullName;
            _assembly = new AssemblyName(assembly);
        }

        public override string? FullName => _fullName;

        public override string Name => _fullName[(_fullName.LastIndexOf('.') + 1)..];

        public override string? Namespace => _fullName[..Math.Max(0, _fullName.LastIndexOf('.'))];

        public override Assembly Assembly => new NamedAssembly(_assembly);

        public override ConstructorInfo[] GetConstructors(BindingFlags bindingAttr) => [];
    }

    private sealed class UnreadableType : TypeDelegator
    {
        private readonly string _fullName;

        public UnreadableType(string fullName)
            : base(typeof(object))
        {
            _fullName = fullName;
        }

        public override string? FullName => _fullName;

        public override string Name => _fullName[(_fullName.LastIndexOf('.') + 1)..];

        public override Assembly Assembly => throw new InvalidOperationException("The controller's assembly can't be read.");
    }

    private sealed class NamedAssembly(AssemblyName name) : Assembly
    {
        public override AssemblyName GetName() => name;

        public override string? FullName => name.FullName;
    }
}
