using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.SmartSearch.Api.Models;
using Jellyfin.Plugin.SmartSearch.AutoCollections;
using Jellyfin.Plugin.SmartSearch.Query;
using Jellyfin.Plugin.SmartSearch.Query.Validation;
using Jellyfin.Plugin.SmartSearch.Services;
using Jellyfin.Plugin.SmartSearch.Storage;
using Jellyfin.Plugin.SmartSearch.Tests.Execution;
using Jellyfin.Plugin.SmartSearch.Tests.Planning;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Jellyfin.Plugin.SmartSearch.Tests.AutoCollections;

/// <summary>
/// The auto-collection service over the test world: stores of its own in a folder, the server's collection calls
/// recorded, and the runs on a queue the test can wait on.
/// </summary>
internal sealed class AutoCollectionHost : IDisposable
{
    private readonly bool _ownsQueries;

    public AutoCollectionHost(
        PlannerWorld world,
        EngineHarness harness,
        string data,
        RecordingPersistence persistence,
        IUserManager users,
        SavedQueryStore? queries = null,
        Func<ISmartSearchSavedQueries>? savedQueryService = null,
        TimeProvider? time = null,
        AutoCollectionSettings? settings = null,
        Func<int>? maxSavedQueriesPerUser = null)
    {
        Settings = settings ?? new AutoCollectionSettings();
        World = world;
        Persistence = persistence;
        Time = time ?? TimeProvider.System;
        Store = AutoCollectionTestData.Store(data);
        State = AutoCollectionTestData.StateStore(data);
        _ownsQueries = queries is null;
        Queries = queries ?? new SavedQueryStore(Path.Combine(data, SavedQueryStore.FileName), Path.Combine(data, "snapshots"), () => new NoSavedQueryReferences(), NullLogger.Instance);
        var registry = harness.Registry;
        var access = world.Core.Access();
        var library = world.Core.Library.Object;
        var collections = new Mock<ICollectionManager>();
        collections.Setup(c => c.GetCollectionsFolder(false)).ReturnsAsync(new Folder { Path = Path.Combine(data, "collections") });
        var fileSystem = new Mock<IFileSystem>();
        fileSystem.Setup(f => f.GetValidFilename(It.IsAny<string>())).Returns((string n) => n);
        var paths = new Mock<IServerApplicationPaths>();
        paths.Setup(p => p.DataPath).Returns(Path.Combine(data, "server-data"));
        var names = new CollectionNameChecker(library, collections.Object, fileSystem.Object, paths.Object, Store);
        var checks = Checks = new AutoCollectionChecks(new QueryValidator(registry), new QueryCanonicalizer(registry), registry);
        Context = new AutoCollectionCheckContextSource(Store, Queries, users, () => QueryLimits.ItemKinds, () => Settings);
        var mixed = new MixedAudienceCheck(access, users);
        Locator = new CollectionLocator(library, access);
        var links = new LinkResolver(library, access);
        var planner = new MembershipPlanner(harness.Engine, access, library);
        var writer = new DirectReplaceWriter(library, persistence, Locator, NullLogger.Instance);
        Guard = new MassRemovalGuard(library, path => !MissingFolders.Contains(path));
        Moves = new MoveJournalReader(Path.Combine(data, "moves.jsonl"), NullLogger.Instance);
        Keeper = new ManualEditKeeper(library, new IdentityResolver(library, Moves, Time), Moves);
        ManagerApi = new ManagerApiWriter(persistence, writer, Suppression, NullLogger.Instance);
        DefaultImage = new AutoCollectionDefaultImage(Images, NullLogger.Instance);
        Runner = new DefinitionRunner(Store, State, checks, Context, Locator, links, planner, mixed, names, writer, Guard, Time, NullLogger.Instance, Keeper, ManagerApi, DefaultImage);
        var server = new Mock<IServerApplicationHost>();
        server.Setup(s => s.CoreStartupHasCompleted).Returns(true);
        Server = server.Object;
        Worker = new AutoCollectionWorker(server.Object, Runner, Store, State, () => Settings, () => ScanRunning, Time, NullLogger.Instance);
        Worker.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        Queue = new TrackingQueue(Worker);
        Collections = collections;
        Listener = new CollectionEditListener(server.Object, collections.Object, library, Store, State, Queue, Keeper, Suppression, () => Settings, () => ScanRunning, Time, NullLogger.Instance);
        Listener.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        Ledger = new PreviewLedger(Time);
        Service = new AutoCollectionService(new AutoCollectionServiceParts
        {
            Names = names,
            Store = Store,
            State = State,
            Checks = checks,
            Context = Context,
            Planner = planner,
            MixedAudience = mixed,
            Hydrator = harness.Hydrator,
            Access = access,
            Runner = Runner,
            Queue = Queue,
            Locator = Locator,
            Links = links,
            SavedQueries = Queries,
            SavedQueryService = savedQueryService ?? (() => throw new InvalidOperationException("No saved-query service here.")),
            Ledger = Ledger,
            Time = Time,
            Guard = Guard,
            ScanRunning = () => ScanRunning,
            Keeper = Keeper,
            MaxSavedQueriesPerUser = maxSavedQueriesPerUser ?? (() => int.MaxValue)
        });
        Transfer = new AutoCollectionTransfer(Store, () => Settings, NullLogger.Instance, Queries);
        Diagnostics = new AutoCollectionDiagnostics(Store, State, Locator);
    }

    /// <summary>Gets the definitions' part of the export and import.</summary>
    public AutoCollectionTransfer Transfer { get; }

    /// <summary>Gets the auto-collections' Diagnostics entry.</summary>
    public AutoCollectionDiagnostics Diagnostics { get; }

    public PlannerWorld World { get; }

    /// <summary>Gets the move journal (a file that doesn't exist until a test writes it).</summary>
    public MoveJournalReader Moves { get; }

    /// <summary>Gets the pins and excludes keeper.</summary>
    public ManualEditKeeper Keeper { get; }

    /// <summary>Gets the writer through the server's collection calls (used when the settings ask for it).</summary>
    public ManagerApiWriter ManagerApi { get; }

    /// <summary>Gets the collections the plugin is writing.</summary>
    public CollectionWriteSuppression Suppression { get; } = new();

    /// <summary>Gets the server's image calls, recorded (the default poster).</summary>
    public RecordingImages Images { get; } = new();

    /// <summary>Gets the default poster.</summary>
    public AutoCollectionDefaultImage DefaultImage { get; }

    /// <summary>Gets the edit listener.</summary>
    public CollectionEditListener Listener { get; }

    /// <summary>Gets the collection manager (its events).</summary>
    public Mock<ICollectionManager> Collections { get; }

    public RecordingPersistence Persistence { get; }

    public TimeProvider Time { get; }

    public AutoCollectionStore Store { get; }

    public AutoCollectionStateStore State { get; }

    public SavedQueryStore Queries { get; }

    public AutoCollectionCheckContextSource Context { get; }

    public CollectionLocator Locator { get; }

    public DefinitionRunner Runner { get; }

    public TrackingQueue Queue { get; }

    public AutoCollectionWorker Worker { get; }

    public AutoCollectionChecks Checks { get; }

    public MassRemovalGuard Guard { get; }

    /// <summary>Gets the library folders the guard finds missing.</summary>
    public HashSet<string> MissingFolders { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets a server whose startup has completed.</summary>
    public IServerApplicationHost Server { get; }

    /// <summary>Gets or sets a value indicating whether a library scan runs, as the worker sees it.</summary>
    public bool ScanRunning { get; set; }

    public PreviewLedger Ledger { get; }

    public AutoCollectionService Service { get; }

    /// <summary>Gets or sets the settings, read on each use.</summary>
    public AutoCollectionSettings Settings { get; set; }

    private static CancellationToken Ct => CancellationToken.None;

    /// <summary>
    /// Answers the library's lookups of collections from the recorded ones: by id, by mark, the list of collections, and
    /// a collection's new id from its path.
    /// </summary>
    public static void SetUpLibrary(PlannerWorld world, RecordingPersistence persistence)
    {
        var library = world.Core.Library;
        library.Setup(l => l.GetItemById<BaseItem>(It.IsAny<Guid>(), It.IsAny<User?>())).Returns((Guid id, User? _) => library.Object.GetItemById(id) ?? persistence.Boxes.FirstOrDefault(b => b.Id.Equals(id)));
        library.Setup(l => l.GetItemById<BoxSet>(It.IsAny<Guid>(), It.IsAny<User?>())).Returns((Guid id, User? _) => persistence.Boxes.FirstOrDefault(b => b.Id.Equals(id)));
        library.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.HasAnyProviderId != null && q.HasAnyProviderId.Count > 0))).Returns((InternalItemsQuery q) =>
            persistence.Boxes.Where(b => q.HasAnyProviderId!.All(p => b.ProviderIds.TryGetValue(p.Key, out var v) && v == p.Value)).Cast<BaseItem>().ToList());
        library.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => (q.HasAnyProviderId == null || q.HasAnyProviderId.Count == 0) && q.IncludeItemTypes.Length == 1 && q.IncludeItemTypes[0] == BaseItemKind.BoxSet)))
            .Returns(() => persistence.Boxes.Cast<BaseItem>().ToList());
        library.Setup(l => l.GetLibraryOptions(It.IsAny<BaseItem>())).Returns(new MediaBrowser.Model.Configuration.LibraryOptions());
        library.Setup(l => l.GetNewItemId(It.IsAny<string>(), It.IsAny<Type>())).Returns((string key, Type type) => new Guid(MD5.HashData(Encoding.Unicode.GetBytes(type.FullName + key.ToLowerInvariant()))));
        library.Setup(l => l.GetVirtualFolders()).Returns([]);
    }

    /// <summary>A definition with a copy of a query.</summary>
    public static SmartSearchAutoCollectionDefinitionDto Def(string? name, string? query = null, string? sortMode = null, Guid? savedQueryId = null)
    {
        var definition = new SmartSearchAutoCollectionDefinitionDto { Name = name };
        if (query is not null)
        {
            var read = QueryJson.Read(query);
            definition.Source = new SmartSearchAutoCollectionSourceDto { Mode = "Snapshot", Read = read, Query = read.Definition };
        }
        else if (savedQueryId is { } id)
        {
            definition.Source = new SmartSearchAutoCollectionSourceDto { Mode = "Reference", SavedQueryId = id };
        }

        if (sortMode is not null)
        {
            definition.Presentation = new SmartSearchAutoCollectionPresentationDto { SortMode = sortMode };
        }

        return definition;
    }

    /// <summary>Previews a definition, then creates it from that preview, and waits for its collection.</summary>
    public async Task<SmartSearchAutoCollectionDto> CreateAsync(User caller, bool isAdmin, SmartSearchAutoCollectionDefinitionDto definition, bool duplicate = false)
    {
        var preview = await Service.PreviewAsync(caller, isAdmin, new SmartSearchAutoCollectionPreviewRequestDto { Definition = definition }, Ct);
        var created = await Service.CreateAsync(caller, isAdmin, new SmartSearchAutoCollectionRequestDto { Definition = definition, AcceptedPreviewHash = preview.PreviewHash, DuplicateSourceQuery = duplicate ? true : null }, Ct);
        await Queue.WhenIdleAsync();
        return await Service.GetAsync(caller, isAdmin, created.Id, Ct);
    }

    /// <summary>Asks for a refresh and waits for it.</summary>
    public async Task<SmartSearchAutoCollectionDto> RefreshAsync(User caller, bool isAdmin, Guid id)
    {
        await Service.RefreshAsync(caller, isAdmin, id, Ct);
        await Queue.WhenIdleAsync();
        return await Service.GetAsync(caller, isAdmin, id, Ct);
    }

    /// <summary>Changes a definition and waits for its run.</summary>
    public async Task<SmartSearchAutoCollectionDto> UpdateAsync(User caller, bool isAdmin, Guid id, SmartSearchAutoCollectionDefinitionDto changes, DateTime ifUpdatedAt, string? acceptedPreviewHash = null)
    {
        await Service.UpdateAsync(caller, isAdmin, id, new SmartSearchAutoCollectionRequestDto { Definition = changes, IfUpdatedAt = ifUpdatedAt, AcceptedPreviewHash = acceptedPreviewHash }, Ct);
        await Queue.WhenIdleAsync();
        return await Service.GetAsync(caller, isAdmin, id, Ct);
    }

    public void Dispose()
    {
        Listener.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        Listener.Dispose();
        Worker.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        Worker.Dispose();
        Runner.Dispose();
        State.Dispose();
        Store.Dispose();
        if (_ownsQueries)
        {
            Queries.Dispose();
        }
    }

    /// <summary>A queue that keeps the runs it was asked for, so a test can wait until they are done.</summary>
    internal sealed class TrackingQueue : IAutoCollectionQueue
    {
        private readonly IAutoCollectionQueue _inner;
        private readonly ConcurrentBag<Task> _tasks = [];

        public TrackingQueue(IAutoCollectionQueue inner) => _inner = inner;

        public List<AutoCollectionRequest> Requests { get; } = [];

        /// <summary>Gets or sets what runs just before a request is passed on (a test changing the world in between).</summary>
        public Action<AutoCollectionRequest>? OnRequest { get; set; }

        public List<SmartSearchAutoCollectionRunReason> Passes { get; } = [];

        public Task<AutoCollectionState> RequestAsync(AutoCollectionRequest request)
        {
            lock (Requests)
            {
                Requests.Add(request);
            }

            OnRequest?.Invoke(request);
            var task = _inner.RequestAsync(request);
            _tasks.Add(task);
            return task;
        }

        public Task RequestPassAsync(SmartSearchAutoCollectionRunReason reason, IProgress<double>? progress = null)
        {
            lock (Passes)
            {
                Passes.Add(reason);
            }

            var task = _inner.RequestPassAsync(reason, progress);
            _tasks.Add(task);
            return task;
        }

        public AutoCollectionActivity? ActivityOf(Guid definitionId) => _inner.ActivityOf(definitionId);

        public async Task WhenIdleAsync()
        {
            foreach (var task in _tasks.ToArray())
            {
                try
                {
                    await task;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // The run's failure is the caller's to look at.
                }
            }
        }
    }
}
