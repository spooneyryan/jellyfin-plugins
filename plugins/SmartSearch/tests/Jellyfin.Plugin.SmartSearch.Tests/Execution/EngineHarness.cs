using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.SmartSearch.Api.Models;
using Jellyfin.Plugin.SmartSearch.Query;
using Jellyfin.Plugin.SmartSearch.Query.Evaluation;
using Jellyfin.Plugin.SmartSearch.Query.Execution;
using Jellyfin.Plugin.SmartSearch.Query.Fields;
using Jellyfin.Plugin.SmartSearch.Query.Planning;
using Jellyfin.Plugin.SmartSearch.Query.Validation;
using Jellyfin.Plugin.SmartSearch.Tests.Planning;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.SmartSearch.Tests.Execution;

/// <summary>
/// The engine as the plugin wires it (<c>PluginServiceRegistrator</c>), over a <see cref="FakeCore"/>: its planner,
/// Residual path, page hydrator, result cache (no events; the tests invalidate it), slots and settings, on a
/// <see cref="ManualTimeProvider"/> whose now is the world's.
/// </summary>
internal sealed class EngineHarness : IDisposable
{
    public EngineHarness(FakeCore core, User admin, User kid, TimeZoneInfo zone, Func<string, ParentalRatingScore?> ratingScore, FieldRegistry? registry = null, IEnumerable<IItemFactsSource>? facts = null, int slots = 2, TimeSpan? slotWait = null, ManualTimeProvider? clock = null, IScopedLookups? lookups = null)
    {
        Clock = clock ?? new ManualTimeProvider();
        Core = core;
        Admin = admin;
        Kid = kid;
        Registry = registry ?? PlanRunner.Registry;
        Validator = new QueryValidator(Registry);
        var access = core.Access();
        var planner = new QueryPlanner(Registry, access, core.Library.Object, core, lookups);
        Residual = new ResidualEvaluator(Registry, core.Library.Object, core.UserData.Object, facts ?? []);
        Hydrator = new PageHydrator(core.Library.Object, core.Dto.Object, access);
        Cache = new ResultCache(null, null, null, Clock, () => TimeSpan.FromSeconds(Settings.ResultCacheSeconds), () => Settings.MaxCachedResultIds);
        Slots = new EngineSlots(slots, slotWait ?? TimeSpan.FromMilliseconds(200));
        Engine = new SmartSearchEngine(
            planner,
            new QueryCanonicalizer(Registry),
            access,
            core.Library.Object,
            Residual,
            Hydrator,
            Cache,
            Slots,
            () => Settings,
            () => new EvaluationEnvironment { NowUtc = Clock.GetUtcNow().UtcDateTime, TimeZone = zone, RatingScore = ratingScore },
            Clock);
    }

    public EngineHarness(PlannerWorld world, FieldRegistry? registry = null, IEnumerable<IItemFactsSource>? facts = null, int slots = 2, TimeSpan? slotWait = null, ManualTimeProvider? clock = null, IScopedLookups? lookups = null)
        : this(world.Core, world.Admin, world.Kid, world.Zone, world.Environment.RatingScore, registry, facts, slots, slotWait, clock, lookups)
    {
    }

    public FakeCore Core { get; }

    public User Admin { get; }

    public User Kid { get; }

    public FieldRegistry Registry { get; }

    public QueryValidator Validator { get; }

    public ManualTimeProvider Clock { get; }

    public EngineSettings Settings { get; set; } = new();

    public ResidualEvaluator Residual { get; }

    public PageHydrator Hydrator { get; }

    public ResultCache Cache { get; }

    public EngineSlots Slots { get; }

    public SmartSearchEngine Engine { get; }

    /// <summary>Reads and validates a query with the harness's registry.</summary>
    public SmartSearchQueryDefinition Read(string json)
    {
        var read = QueryJson.Read(json);
        Xunit.Assert.True(read.Definition is not null, json + " → " + string.Join(", ", read.Errors.Select(e => e.Path + ":" + e.Code)));
        var validation = Validator.Validate(read.Definition!, QueryValidationContext.Admin);
        Xunit.Assert.True(validation.Valid, json + " → " + string.Join(", ", validation.Errors.Select(e => e.Path + ":" + e.Code)));
        return read.Definition!;
    }

    /// <summary>Searches with the test's cancellation token.</summary>
    public Task<SmartSearchPage> Search(SmartSearchQueryDefinition query, User user, int startIndex = 0, int limit = 50, string? token = null)
        => Engine.SearchAsync(query, user, new PageRequest(startIndex, limit, token), Xunit.TestContext.Current.CancellationToken);

    /// <summary>Searches with a token of the test's own.</summary>
    public Task<SmartSearchPage> Search(SmartSearchQueryDefinition query, User user, CancellationToken cancellationToken)
        => Engine.SearchAsync(query, user, new PageRequest(), cancellationToken);

    /// <summary>Pages through a search to its end with the first page's token, as the page does.</summary>
    public async Task<EngineRun> SearchAll(SmartSearchQueryDefinition query, User user, int pageSize = 50)
    {
        var ids = new List<Guid>();
        var warnings = new List<string>();
        var pages = new List<SmartSearchPage>();
        string? token = null;
        SmartSearchPage page;
        var start = 0;
        do
        {
            page = await Search(query, user, start, pageSize, token).ConfigureAwait(false);
            pages.Add(page);
            token = page.ResultToken;
            ids.AddRange(page.Items.Select(i => i.Id));
            warnings.AddRange(page.Warnings);
            start += pageSize;
            Xunit.Assert.True(pages.Count < 10_000, "the pages never end");
        }
        while (page.HasMore);

        return new EngineRun(ids, pages[0].TotalRecordCount, pages, warnings, pages[0].Path);
    }

    public void Dispose()
    {
        Cache.Dispose();
        Slots.Dispose();
    }
}

/// <summary>A search paged to its end: the ids shown in order, the first page's count, the pages and their warnings.</summary>
internal sealed record EngineRun(List<Guid> Ids, int? Total, List<SmartSearchPage> Pages, List<string> Warnings, QueryPlanPath Path)
{
    /// <summary>Gets the pages that came back shorter than their rows (an item dropped by the hydrator).</summary>
    public int ShortPages => Pages.Count(p => p.Dropped > 0);
}
