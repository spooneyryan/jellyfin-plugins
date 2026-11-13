using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.SmartSearch.Api.Models;
using Jellyfin.Plugin.SmartSearch.Query;
using Jellyfin.Plugin.SmartSearch.Query.Evaluation;
using Jellyfin.Plugin.SmartSearch.Query.Fields;
using Jellyfin.Plugin.SmartSearch.Query.Planning;
using Jellyfin.Plugin.SmartSearch.Query.Validation;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.SmartSearch.Tests.Planning;

/// <summary>
/// Runs a plan over <see cref="FakeCore"/> the way the engine will. Residual: the
/// candidate ids in one call, hydration in batches of 1,000 with the plan's <c>DtoOptions</c> (user data only when the
/// plan asks for it, provider ids only when it asks for them; <c>GetItemList</c> drops what <c>IsVisible</c> refuses),
/// then the re-check. Fast: <c>GetItemsResult</c> a page at a time with core's count, then what
/// <c>DtoService.GetBaseItemDtos</c> does to a page (it drops what <c>IsVisible(user)</c> refuses, DtoService.cs:158), so
/// a page can come back short. And the truth it must equal: every row <c>/Items</c> would show the user (SQL's access,
/// then <c>IsVisible</c>), in the scope, not owned, that the whole tree matches in memory, with a library membership of
/// the test's own; and what <c>/Items</c> shows and counts, which the results and the count must never be wider than.
/// </summary>
internal static class PlanRunner
{
    public const int BatchSize = 1000;

    public static readonly FieldRegistry Registry = new();

    public static readonly QueryValidator Validator = new(Registry);

    public static SmartSearchQueryDefinition Read(string json)
    {
        var read = QueryJson.Read(json);
        Assert.True(read.Definition is not null, json + " → " + string.Join(", ", read.Errors.Select(e => e.Path + ":" + e.Code)));
        var validation = Validator.Validate(read.Definition!, QueryValidationContext.Admin);
        Assert.True(validation.Valid, json + " → " + string.Join(", ", validation.Errors.Select(e => e.Path + ":" + e.Code)));
        return read.Definition!;
    }

    /// <summary>Reads a query, or returns <c>null</c> when the validator refuses it (the generator's queries).</summary>
    public static SmartSearchQueryDefinition? TryRead(string json) => TryRead(json, out _);

    /// <summary>Reads a query, or returns <c>null</c> and the errors' codes when it doesn't read or validate.</summary>
    public static SmartSearchQueryDefinition? TryRead(string json, out IReadOnlyList<string> codes)
    {
        var read = QueryJson.Read(json);
        if (read.Definition is not { } definition)
        {
            codes = read.Errors.Select(e => "read:" + e.Code).ToList();
            return null;
        }

        var validation = Validator.Validate(definition, QueryValidationContext.Admin);
        codes = validation.Errors.Select(e => System.Text.RegularExpressions.Regex.Replace(e.Path, @"\[\d+\]", "[]") + ":" + e.Code).ToList();
        return validation.Valid ? definition : null;
    }

    /// <summary>The plan's results: ids for the Residual path, presentation keys' rows for the Fast path.</summary>
    public static HashSet<Guid> Run(QueryPlan plan, FakeCore core, User? user) => Execute(plan, core, user).Results;

    /// <summary>
    /// Runs a plan: the results, the count the engine reports (core's <c>TotalRecordCount</c> on the Fast path, which
    /// counts in SQL, before <c>IsVisible</c>; the results' on the others) and the rows that count counts.
    /// </summary>
    /// <param name="plan">The plan.</param>
    /// <param name="core">The fake core.</param>
    /// <param name="user">The user.</param>
    /// <param name="pageSize">The Fast path's page size (the engine's is the query's <c>Limit</c>, at most 200).</param>
    /// <returns>What the run returned.</returns>
    public static PlanRun Execute(QueryPlan plan, FakeCore core, User? user, int pageSize = QueryPlanner.PageWindow)
    {
        var results = new HashSet<Guid>();
        if (plan.Path == QueryPlanPath.Empty)
        {
            return new PlanRun(results, 0, [], 0, 0);
        }

        var library = core.Library.Object;
        if (plan.Path == QueryPlanPath.Fast)
        {
            var query = plan.CandidateQuery!;
            var counted = new HashSet<Guid>();
            int? total = null;
            var pages = 0;
            var shortPages = 0;
            try
            {
                for (var start = 0; total is null || start < total; start += pageSize)
                {
                    query.StartIndex = start;
                    query.Limit = pageSize;
                    query.EnableTotalRecordCount = true;
                    var page = library.GetItemsResult(query);
                    Assert.True(total is null || total == page.TotalRecordCount, "the count changed between pages");
                    total = page.TotalRecordCount;
                    pages++;
                    counted.UnionWith(page.Items.Select(i => i.Id));
                    var shown = user is null ? page.Items : page.Items.Where(i => core.IsVisibleTo(core.RowById(i.Id)!, user)).ToList();
                    if (shown.Count < page.Items.Count)
                    {
                        shortPages++;
                    }

                    foreach (var item in shown)
                    {
                        if (plan.Recheck(Row(plan, core, user, item)))
                        {
                            results.Add(item.Id);
                        }
                    }

                    if (page.Items.Count == 0)
                    {
                        break;
                    }
                }
            }
            finally
            {
                query.StartIndex = null;
                query.Limit = null;
            }

            Assert.True(total == counted.Count, "the pages held " + counted.Count + " rows and the count is " + total);
            return new PlanRun(results, total!.Value, counted, pages, shortPages);
        }

        var ids = library.GetItemIds(plan.CandidateQuery!);
        foreach (var batch in ids.Chunk(BatchSize))
        {
            foreach (var item in library.GetItemList(plan.CreateHydrationQuery(batch)))
            {
                if (plan.Recheck(Row(plan, core, user, item)))
                {
                    results.Add(item.Id);
                }
            }
        }

        return new PlanRun(results, results.Count, results, 0, 0);
    }

    /// <summary>
    /// What core's <c>/Items</c> does for the user and the scope (recursive, the scope's kinds, missing items as asked):
    /// the rows SQL's access lets through, the ones it shows (<c>DtoService.GetBaseItemDtos</c> drops what
    /// <c>IsVisible(user)</c> refuses), and the ones its <c>TotalRecordCount</c> counts: SQL's, except for a BoxSet-only
    /// query, which has no parent (ItemsController.cs:273-278) and so goes through <c>Folder.QueryWithPostFiltering2</c>,
    /// which filters in memory and counts what is left.
    /// </summary>
    public static ItemsView ItemsEndpoint(SmartSearchQueryDefinition query, FakeCore core, User? user)
    {
        var access = AccessQuery(user);
        var folders = core.VisibleLibraries(user).SelectMany(l => l.PhysicalFolderIds).ToHashSet();
        var sql = core.Rows
            .Where(r => r.TopParentId is { } top && folders.Contains(top) && query.Scope.ItemKinds.Contains(r.Kind) && (query.Scope.IncludeMissing || !r.IsVirtual) && core.PassesAccess(r, access))
            .Select(r => r.Id)
            .ToHashSet();
        var shown = user is null ? sql : sql.Where(id => core.IsVisibleTo(core.RowById(id)!, user)).ToHashSet();
        var boxSetsOnly = user is not null && query.Scope.ItemKinds.Count == 1 && query.Scope.ItemKinds.Contains(BaseItemKind.BoxSet);
        return new ItemsView(sql, shown, boxSetsOnly ? shown : sql);
    }

    public static HashSet<Guid> Truth(SmartSearchQueryDefinition query, FakeCore core, User? user, EvaluationEnvironment environment)
    {
        var scope = query.Scope;
        var visible = core.VisibleLibraries(user);
        var allowed = visible
            .Where(l => scope.LibraryIds.Count == 0 || scope.LibraryIds.Contains(l.Id))
            .Where(l => l.CollectionType != CollectionType.boxsets || scope.ItemKinds.Contains(BaseItemKind.BoxSet))
            .ToList();
        var folders = allowed.SelectMany(l => l.PhysicalFolderIds).ToHashSet();
        bool InScope(FakeRow r) => scope.ItemKinds.Contains(r.Kind) && (scope.IncludeMissing || !r.IsVirtual);
        IReadOnlySet<Guid> Members(IReadOnlyCollection<Guid> libraries)
        {
            var theirs = visible.Where(l => libraries.Contains(l.Id)).SelectMany(l => l.PhysicalFolderIds).ToHashSet();
            return core.Rows.Where(r => r.TopParentId is { } top && theirs.Contains(top) && InScope(r)).Select(r => r.Id).ToHashSet();
        }

        // A People rule's truth: the rows with a credit of the person's name (the Person item of the reference's id, else
        // of its name), in the role when one is named.
        Func<Guid, bool> People(Guid id, string? name, string? role)
        {
            var person = core.PersonName(id, name);
            var set = person is null ? new HashSet<Guid>() : core.Rows.Where(r => r.Credits.Any(c => c.Name == person && (role is null || c.Type == role))).Select(r => r.Id).ToHashSet();
            return set.Contains;
        }

        var test = RuleTree.Compile(query.Where, Registry, new EvaluationEnvironment
        {
            NowUtc = environment.NowUtc,
            TimeZone = environment.TimeZone,
            RatingScore = environment.RatingScore,
            LibraryMembers = Members,
            PeopleMembers = People
        });
        var access = AccessQuery(user);
        return core.Rows
            .Where(r => r.TopParentId is { } top && folders.Contains(top) && InScope(r) && r.Item.OwnerId == Guid.Empty && core.PassesAccess(r, access))
            .Where(r => user is null || core.IsVisibleTo(r, user))
            .Where(r => test(new EvaluationRow(r.Item, user is null ? null : core.MemoryUserData(user, r))))
            .Select(r => r.Id)
            .ToHashSet();
    }

    /// <summary>
    /// The truth's rows the results lack (false negatives) and the results the truth lacks; on the Fast path by
    /// presentation key, since SQL keeps one row per key.
    /// </summary>
    public static (List<string> Missing, List<string> Extra) Compare(QueryPlan plan, IReadOnlySet<Guid> truth, IReadOnlySet<Guid> results, FakeCore core)
    {
        string Name(Guid id) => core.RowById(id)!.Item.Name + "#" + id.ToString("N")[..6];
        if (plan.Path == QueryPlanPath.Fast)
        {
            var truthKeys = Keys(truth, core);
            var resultKeys = Keys(results, core);
            return (
                [.. truth.Where(id => !resultKeys.Contains(core.RowById(id)!.PresentationUniqueKey)).Select(Name)],
                [.. results.Where(id => !truthKeys.Contains(core.RowById(id)!.PresentationUniqueKey)).Select(Name)]);
        }

        return ([.. truth.Where(id => !results.Contains(id)).Select(Name)], [.. results.Where(id => !truth.Contains(id)).Select(Name)]);
    }

    /// <summary>The presentation keys of a set of rows (the Fast path's results are one row per key).</summary>
    public static HashSet<string> Keys(IEnumerable<Guid> ids, FakeCore core) => ids.Select(id => core.RowById(id)!.PresentationUniqueKey).ToHashSet(StringComparer.Ordinal);

    // ItemsController.GetItems: new InternalItemsQuery(user), then BlockUnratedItems from the user's preferences.
    private static InternalItemsQuery AccessQuery(User? user)
    {
        var query = user is null ? new InternalItemsQuery() : new InternalItemsQuery(user);
        if (user is not null)
        {
            query.BlockUnratedItems = user.GetPreferenceValues<UnratedItem>(PreferenceKind.BlockUnratedItems);
        }

        return query;
    }

    private static EvaluationRow Row(QueryPlan plan, FakeCore core, User? user, BaseItem item)
        => new(item, plan.NeedsUserData && user is not null ? core.MemoryUserData(user, core.RowById(item.Id)!) : null);
}

/// <summary>A plan's run: the results, the count reported, the rows it counts, and the Fast path's pages and short pages.</summary>
internal sealed record PlanRun(HashSet<Guid> Results, int Count, HashSet<Guid> Counted, int Pages, int ShortPages);

/// <summary>What <c>/Items</c> does for a scope: the rows SQL lets through, the ones it shows, and the ones it counts.</summary>
internal sealed record ItemsView(HashSet<Guid> Sql, HashSet<Guid> Shown, HashSet<Guid> Counted);
