using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Extensions.Json;
using Jellyfin.Plugin.Kit.Diagnostics;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.Api;
using Jellyfin.Plugin.Kit.TestSupport.Web;
using Jellyfin.Plugin.Kit.Users;
using Jellyfin.Plugin.Kit.Web;
using Jellyfin.Plugin.SmartSearch.Api;
using Jellyfin.Plugin.SmartSearch.AutoCollections;
using Jellyfin.Plugin.SmartSearch.Query;
using Jellyfin.Plugin.SmartSearch.Query.Fields;
using Jellyfin.Plugin.SmartSearch.Search;
using Jellyfin.Plugin.SmartSearch.Services;
using Jellyfin.Plugin.SmartSearch.Storage;
using Jellyfin.Plugin.SmartSearch.Tests.AutoCollections;
using Jellyfin.Plugin.SmartSearch.Tests.Execution;
using Jellyfin.Plugin.SmartSearch.Tests.Planning;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.SmartSearch.Tests.Api;

/// <summary>
/// The search and lookup endpoints behind Jellyfin's web pipeline (<see cref="WebHostHarness"/>: its authentication
/// claims, policies and JSON options), over the real engine, value index and series picker on a
/// <see cref="PlannerWorld"/>: an administrator, the restricted Kid (no Hidden, Hidden Shows or Private; a parental limit
/// of 13; unrated movies blocked; the tag "Gore" blocked) and Kid's twin (the same parental profile, every library).
/// The saved queries live in a temporary folder, with the kit's users services over <see cref="Users"/> (a user removed
/// from it is a deleted user).
/// </summary>
internal sealed class SearchApiHost : IAsyncDisposable
{
    private readonly Mock<IUserManager> _users = new();

    public SearchApiHost(PlannerWorld? world = null, int slots = 2, TimeSpan? slotWait = null)
    {
        World = world ?? new PlannerWorld();
        World.Core.Library.SetupGet(l => l.IsScanRunning).Returns(() => ScanRunning);
        var access = World.Core.Access();
        Values = new ValueIndexService(World.Core.Library.Object, access, Clock);
        Engine = new EngineHarness(World, slots: slots, slotWait: slotWait, clock: Clock, lookups: Values);
        Twin = PlannerWorld.NewUser("twin");
        Twin.MaxParentalRatingScore = World.Kid.MaxParentalRatingScore;
        Twin.SetPreference(PreferenceKind.BlockUnratedItems, [nameof(UnratedItem.Movie), nameof(UnratedItem.Other)]);
        Twin.SetPreference(PreferenceKind.BlockedTags, ["Gore"]);
        World.Admin.SetPermission(PermissionKind.IsAdministrator, true); // the stored flag an owner change reads for its new owner
        Users = [World.Admin, World.Kid, Twin];
        _users.Setup(m => m.GetUserById(It.IsAny<Guid>())).Returns((Guid id) => Users.Find(u => u.Id == id));
        _users.SetupGet(m => m.Users).Returns(() => Users.ToList());

        Series = new SeriesTypeahead(Values, access);
        Service = new SmartSearchService(Engine.Engine, Engine.Validator, access, World.Core.Library.Object, () => AllowedKinds, () => Enabled);
        Lookups = new SmartSearchLookups(access, Engine.Registry, Values, Series, () => AllowedKinds, () => Enabled);
        Fields = new SmartSearchFields(Engine.Registry, () => AllowedKinds, () => Enabled);

        Store = new SavedQueryStore(Data.Combine("smartsearch", SavedQueryStore.FileName), Data.Combine("smartsearch", "snapshots"), () => Guard, NullLogger.Instance, Clock);
        Reader = new SavedQueryReader(Engine.Validator, new QueryCanonicalizer(Engine.Registry));
        SavedQueries = new SavedQueryService(
            Store,
            Reader,
            () => Guard,
            Engine.Validator,
            Service,
            _users.Object,
            () => new SavedQuerySettings(Enabled, AllowSharing, MaxPerUser, AllowedKinds),
            Clock,
            NullLogger.Instance);
        Marks = new KitUserMarks(Data.Combine("smartsearch", KitUsersDocument.FileName), NullLogger.Instance, Clock);
        // The auto-collections' definitions before the saved queries, as the plugin registers them.
        Reconciler = new UserReconciler(
            () => Users.ToDictionary(u => u.Id, u => u.Username),
            () => AutoCollections is null ? [Store] : [AutoCollections.Store, Store],
            static () => [],
            Marks,
            () => Policy,
            NullLogger.Instance,
            Clock);
        var server = new Mock<IServerApplicationHost>();
        server.SetupGet(h => h.SystemId).Returns("server-1");
        SavedQueriesAdmin = new SavedQueryAdmin(
            Store,
            Reader,
            () => Guard,
            Reconciler,
            _users.Object,
            server.Object,
            () => new { AllowQuerySharing = AllowSharing },
            () => new SavedQuerySettings(Enabled, AllowSharing, MaxPerUser, AllowedKinds),
            Clock,
            NullLogger.Instance,
            autoCollections: () => AutoCollections?.Transfer);
    }

    public PlannerWorld World { get; }

    /// <summary>Gets the clock of the engine and the value index.</summary>
    public ManualTimeProvider Clock { get; } = new();

    public EngineHarness Engine { get; }

    public User Admin => World.Admin;

    public User Kid => World.Kid;

    /// <summary>Gets a user with Kid's parental profile who can open every library.</summary>
    public User Twin { get; }

    public ValueIndexService Values { get; }

    public SeriesTypeahead Series { get; }

    public SmartSearchService Service { get; }

    public SmartSearchLookups Lookups { get; }

    public SmartSearchFields Fields { get; }

    public bool Enabled { get; set; } = true;

    /// <summary>Gets the server's users: one removed is a deleted user.</summary>
    public List<User> Users { get; }

    /// <summary>Gets the folder of the saved queries' files.</summary>
    public TempDirectory Data { get; } = new();

    public SavedQueryStore Store { get; }

    public SavedQueryReader Reader { get; }

    /// <summary>Gets or sets the reference guard the saved queries ask (none references a query by default).</summary>
    public ISavedQueryReferenceGuard Guard { get; set; } = new NoSavedQueryReferences();

    public SavedQueryService SavedQueries { get; }

    public SavedQueryAdmin SavedQueriesAdmin { get; }

    public KitUserMarks Marks { get; }

    public UserReconciler Reconciler { get; }

    public bool AllowSharing { get; set; } = true;

    public int MaxPerUser { get; set; } = SavedQuerySettings.DefaultMaxPerUser;

    public DeletedUserPolicy Policy { get; set; } = DeletedUserPolicy.Delete;

    public bool ScanRunning { get; set; }

    public IReadOnlyList<BaseItemKind> AllowedKinds { get; set; } = QueryLimits.ItemKindOrder;

    public WebHostHarness? Web { get; private set; }

    /// <summary>Gets the auto-collections, once <see cref="UseAutoCollections"/> made them.</summary>
    public AutoCollectionHost? AutoCollections { get; private set; }

    /// <summary>Gets the server's collection calls the auto-collections made.</summary>
    public RecordingPersistence Collections { get; } = new();

    /// <summary>Adds the auto-collections over this host's world and saved queries; call before <see cref="StartAsync"/>.</summary>
    public AutoCollectionHost UseAutoCollections()
    {
        AutoCollectionHost.SetUpLibrary(World, Collections);
        AutoCollections = new AutoCollectionHost(World, Engine, Data.Combine("autocollections"), Collections, _users.Object, Store, () => SavedQueries, Clock, maxSavedQueriesPerUser: () => MaxPerUser);
        return AutoCollections;
    }

    /// <summary>Gets the triggers that run what follows a changed saved query, once <see cref="UseReferenceGuard"/> made them.</summary>
    public AutoCollectionTriggers? Triggers { get; private set; }

    /// <summary>
    /// Adds the auto-collections, their definitions as the saved queries' reference guard, and the triggers that run the
    /// auto-collections whose saved query changed (started with the host); call before <see cref="StartAsync"/>.
    /// </summary>
    public AutoCollectionHost UseReferenceGuard()
    {
        var auto = AutoCollections ?? UseAutoCollections();
        Guard = new DefinitionReferenceGuard(auto.Store, Store, auto.Checks, auto.Context, Clock, NullLogger.Instance);
        Triggers = new AutoCollectionTriggers(auto.Server, auto.Queue, auto.Store, Store, () => auto.Settings, Clock, NullLogger.Instance);
        return auto;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async Task<SearchApiHost> StartAsync()
    {
        // The kit's own registrations the controllers' attributes read (KitAbiGate); everything else is this host's.
        var applicationHost = new Mock<IServerApplicationHost>();
        applicationHost.SetupGet(h => h.ApplicationVersion).Returns(new Version(10, 11, 5));
        applicationHost.SetupGet(h => h.CoreStartupHasCompleted).Returns(true);
        var registered = new ServiceCollection();
        new PluginServiceRegistrator().RegisterServices(registered, applicationHost.Object);
        Web = await new WebHostHarness()
            .WithServices(services =>
            {
                foreach (var descriptor in registered.Where(d => d.ServiceType == typeof(KitOptions) || d.ServiceType == typeof(KitAbiStatus)))
                {
                    services.Add(descriptor);
                }

                services.AddSingleton(_users.Object);
                services.AddSingleton(Mock.Of<IEmbeddedScript>());
                services.AddSingleton<ISmartSearchFields>(Fields);
                services.AddSingleton<ISmartSearchService>(Service);
                services.AddSingleton<ISmartSearchLookups>(Lookups);
                services.AddSingleton(new SearchPageSwitch(() => Enabled));
                services.AddSingleton<ISmartSearchSavedQueries>(SavedQueries);
                services.AddSingleton<ISmartSearchSavedQueriesAdmin>(SavedQueriesAdmin);
                services.AddSingleton(Mock.Of<IKitDiagnostics>());
                if (AutoCollections is not null)
                {
                    services.AddSingleton<ISmartSearchAutoCollections>(AutoCollections.Service);
                }
                services.Configure<Microsoft.AspNetCore.Mvc.JsonOptions>(o =>
                {
                    var jellyfin = JsonDefaults.PascalCaseOptions;
                    o.JsonSerializerOptions.DefaultIgnoreCondition = jellyfin.DefaultIgnoreCondition;
                    o.JsonSerializerOptions.PropertyNamingPolicy = jellyfin.PropertyNamingPolicy;
                    o.JsonSerializerOptions.Converters.Clear();
                    foreach (var converter in jellyfin.Converters)
                    {
                        o.JsonSerializerOptions.Converters.Add(converter);
                    }
                });
            })
            .WithMvc(typeof(SmartSearchController), typeof(SmartSearchLookupController), typeof(SmartSearchQueriesController), typeof(SmartSearchAdminController), typeof(SmartSearchAutoCollectionsController))
            .StartAsync();
        if (Triggers is not null)
        {
            await Triggers.StartAsync(Ct);
            var until = DateTime.UtcNow.AddSeconds(10);
            while (!Triggers.IsListening && DateTime.UtcNow < until)
            {
                await Task.Delay(10, Ct);
            }
        }

        return this;
    }

    /// <summary>Sends a request as a caller: <c>admin</c>, <c>user</c>, <c>apikey</c>, or <c>null</c> for none.</summary>
    public Task<HttpResponseMessage> SendRawAsync(string method, string path, string? caller, User? user = null, string? json = null)
        => Web!.SendAsync(new HttpMethod(method), path, request =>
        {
            if (caller is not null)
            {
                request.Headers.Add(JellyfinAuth.CallerHeader, caller);
            }

            if (user is not null)
            {
                request.Headers.Add(JellyfinAuth.UserIdHeader, user.Id.ToString("N"));
            }

            if (json is not null)
            {
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }
        });

    /// <summary>Sends a request as a user (<c>admin</c> for the administrator) and reads the JSON answer.</summary>
    public async Task<(HttpStatusCode Status, JsonElement Body, string Text)> SendAsync(string method, string path, User user, string? json = null)
    {
        using var response = await SendRawAsync(method, path, user == Admin ? "admin" : "user", user, json);
        var text = await response.Content.ReadAsStringAsync(Ct);
        using var document = JsonDocument.Parse(text.Length == 0 ? "null" : text);
        return (response.StatusCode, document.RootElement.Clone(), text);
    }

    /// <summary>Sends a request that must succeed.</summary>
    public async Task<JsonElement> OkAsync(string method, string path, User user, string? json = null)
    {
        var (status, body, text) = await SendAsync(method, path, user, json);
        Assert.True(status == HttpStatusCode.OK, $"{method} {path}: {status} {text}");
        return body;
    }

    /// <summary>A search, every page, with the first page's token; the ids in order.</summary>
    public async Task<List<Guid>> SearchAllAsync(string query, User user, int limit = 50)
    {
        var ids = new List<Guid>();
        string? token = null;
        for (var start = 0; ; start += limit)
        {
            var page = await OkAsync("POST", "/SmartSearch/Search", user, Body(query, start, limit, token));
            ids.AddRange(page.GetProperty("Items").EnumerateArray().Select(i => Guid.Parse(i.GetProperty("Id").GetString()!)));
            token = page.GetProperty("ResultToken").GetString();
            if (!page.GetProperty("HasMore").GetBoolean())
            {
                return ids;
            }

            Assert.True(start < 10_000, "the pages never end");
        }
    }

    public static string Body(string query, int startIndex = 0, int limit = 50, string? token = null)
        => "{\"Query\":" + query + ",\"StartIndex\":" + startIndex + ",\"Limit\":" + limit + (token is null ? string.Empty : ",\"ResultToken\":\"" + token + "\"") + "}";

    public async ValueTask DisposeAsync()
    {
        if (Web is not null)
        {
            await Web.DisposeAsync();
        }

        if (Triggers is not null)
        {
            await Triggers.StopAsync(CancellationToken.None);
            Triggers.Dispose();
        }

        AutoCollections?.Dispose();
        Values.Dispose();
        Engine.Dispose();
        Reconciler.Dispose();
        Marks.Dispose();
        Store.Dispose();
        Data.Dispose();
    }
}
