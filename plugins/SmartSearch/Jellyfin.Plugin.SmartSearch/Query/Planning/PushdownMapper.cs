using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.SmartSearch.Api.Models;
using Jellyfin.Plugin.SmartSearch.Query.Evaluation;
using Jellyfin.Plugin.SmartSearch.Query.Fields;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.SmartSearch.Query.Planning;

/// <summary>
/// Maps one rule to its SQL filter, each one Exact, Superset or
/// declined, following core 10.11.5's SQL as it is, not as it is documented. A mapping is Exact only
/// when every kind the query can return is one of the field's kinds; per-user filters are never pushed
/// otherwise, and never as <c>= false</c>.
/// </summary>
internal sealed class PushdownMapper
{
    /// <summary>The widening of every pushed date bound.</summary>
    public static readonly TimeSpan DateSlack = TimeSpan.FromDays(1);

    private static readonly (string Bucket, int Min, int? Max)[] Buckets =
    [
        ("SD", 1, 1199), ("720p", 1200, 1899), ("1080p", 1900, 2499), ("1440p", 2500, 3799), ("2160p", 3800, null)
    ];

    private readonly PlanningScope _scope;
    private readonly IPushdownFacts _facts;
    private readonly IScopedLookups _lookups;
    private readonly ILibraryManager _library;
    private readonly FieldRegistry _registry;
    private readonly EvaluationEnvironment _environment;
    private readonly CancellationToken _cancellationToken;
    private IReadOnlyList<ItemValueRow>? _pipes;
    private bool _pipesRead;

    /// <summary>
    /// Initializes a new instance of the <see cref="PushdownMapper"/> class.
    /// </summary>
    /// <param name="scope">The plan's scope.</param>
    /// <param name="facts">What the planner reads from core's database.</param>
    /// <param name="lookups">What it looks up in the caller's scope (the series names and studios they can see).</param>
    /// <param name="library">The library manager (studio ids).</param>
    /// <param name="registry">The field registry (the <c>SeriesName</c> resolver compiles its rule).</param>
    /// <param name="environment">The evaluation environment.</param>
    /// <param name="cancellationToken">The evaluation's cancellation token, passed to every database read.</param>
    public PushdownMapper(PlanningScope scope, IPushdownFacts facts, IScopedLookups lookups, ILibraryManager library, FieldRegistry registry, EvaluationEnvironment environment, CancellationToken cancellationToken = default)
    {
        _scope = scope;
        _facts = facts;
        _lookups = lookups;
        _library = library;
        _registry = registry;
        _environment = environment;
        _cancellationToken = cancellationToken;
    }

    /// <summary>Maps a rule.</summary>
    /// <param name="rule">The rule, validated.</param>
    /// <param name="field">Its field.</param>
    /// <returns>The mapping.</returns>
    public RuleMapping Map(SmartSearchQueryNode rule, FieldDescriptor field)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(field);
        var op = RuleValues.Op(rule);
        var mapping = field.Id switch
        {
            "Title" => Title(rule, op),
            "Overview" => op switch
            {
                SmartSearchQueryOperator.IsNotEmpty => RuleMapping.Superset(Flag(PushProperty.HasOverview, true), "SQL counts a blank overview as present"),
                SmartSearchQueryOperator.IsEmpty => RuleMapping.Declined("SQL drops a blank overview, which the evaluator calls empty"),
                _ => RuleMapping.Declined("no SQL filter")
            },
            "Year" => Year(rule, op),
            "PremiereDate" => PremiereDate(rule, op),
            "DateAdded" => DateAdded(rule, op),
            "Library" => Library(rule, op),
            "ItemType" => ItemType(rule, op),
            "Genres" => Values(rule, op, PushProperty.Genres, ItemValueType.Genre, excludable: false),
            "Tags" => Values(rule, op, PushProperty.Tags, ItemValueType.Tags, excludable: true),
            "Studios" => Studios(rule, op),
            "OfficialRating" => op == SmartSearchQueryOperator.AnyOf
                ? RuleMapping.Exact(PushdownSet.Of(PushProperty.OfficialRatings, new InSetConstraint<string>(RuleValues.Strings(rule), StringComparer.Ordinal, singleValued: true)))
                : RuleMapping.Declined("no SQL filter for this operator"),
            "CommunityRating" => CommunityRating(rule, op),
            "Resolution" => Resolution(rule, op),
            "Played" => UserData(rule, field, PushProperty.IsPlayed, UserDataFlag.Played),
            "Favorite" => UserData(rule, field, PushProperty.IsFavorite, UserDataFlag.Favorite),
            "InProgress" => UserData(rule, field, PushProperty.IsResumable, UserDataFlag.Resumable),
            "HasProviderId" => op == SmartSearchQueryOperator.Is && RuleValues.Bool(rule)
                ? Provider(rule, "SQL counts a blank id")
                : RuleMapping.Declined("Is false isn't pushed: SQL counts a blank id as present"),
            "ProviderId" => Provider(rule, "only the key is pushed; the value is compared in memory (SQL's HasAnyProviderId is case-sensitive)"),
            "Locked" => RuleMapping.Exact(Flag(PushProperty.IsLocked, RuleValues.Bool(rule))),
            "Series" => Series(rule, op),
            "SeriesName" => SeriesName(rule),
            _ => RuleMapping.Declined("no SQL filter")
        };

        // Kinds: Exact only when every kind the query can return is one of the field's kinds.
        return mapping.Exactness == PushdownExactness.Exact && !_scope.Kinds.All(field.Kinds.Contains)
            ? mapping.AtMostSuperset("the query's kinds reach beyond the field's")
            : mapping;
    }

    private static PushdownSet Flag(PushProperty property, bool value) => PushdownSet.Of(property, new ValueConstraint<bool>(value));

    private static PushdownSet Ids(PushProperty property, IEnumerable<Guid> ids, bool singleValued)
        => PushdownSet.Of(property, new InSetConstraint<Guid>(ids, EqualityComparer<Guid>.Default, singleValued));

    private static PushdownSet DateBound(PushProperty property, DateTime utc, bool lower)
        => PushdownSet.Of(property, new BoundConstraint<DateTime>(DateTime.SpecifyKind(utc, DateTimeKind.Utc), lower));

    // A day d stands for its whole calendar day in any zone: from d − 1 day (00:00 UTC) to d + 2 days, so an item on day d
    // in UTC−12 … UTC+14, or by the calendar rule, is inside; an instant t is widened by a day each way.
    private static DateTime LowerEdge(QueryDate date, bool afterTheDay)
    {
        var start = date.IsDay ? date.Day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddDays(afterTheDay ? 1 : 0) : date.Utc;
        return Shift(start, -DateSlack);
    }

    private static DateTime UpperEdge(QueryDate date, bool beforeTheDay)
    {
        var end = date.IsDay ? date.Day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddDays(beforeTheDay ? -1 : 0).AddDays(2) : date.Utc.Add(DateSlack);
        return Shift(end, TimeSpan.Zero);
    }

    private static DateTime Shift(DateTime value, TimeSpan offset)
    {
        var ticks = Math.Clamp(value.Ticks + offset.Ticks, DateTime.MinValue.Ticks, DateTime.MaxValue.Ticks);
        return new DateTime(ticks, DateTimeKind.Utc);
    }

    private static IEnumerable<int> YearRange(double from, double to)
    {
        var lo = (int)Math.Max(QueryLimits.MinYear, Math.Ceiling(from));
        var hi = (int)Math.Min(QueryLimits.MaxYear, Math.Floor(to));
        return lo > hi ? [] : Enumerable.Range(lo, hi - lo + 1);
    }

    private static RuleMapping Year(SmartSearchQueryNode rule, SmartSearchQueryOperator op)
    {
        // Years outside 1800–2100 count as no year, so the enumerated list is exactly the rule.
        IEnumerable<int>? years = op switch
        {
            SmartSearchQueryOperator.Eq => YearRange(RuleValues.Number(rule, rule.Value), RuleValues.Number(rule, rule.Value)),
            SmartSearchQueryOperator.AnyOf => RuleValues.Numbers(rule).SelectMany(v => YearRange(v, v)),
            SmartSearchQueryOperator.Between => YearRange(RuleValues.Number(rule, rule.From), RuleValues.Number(rule, rule.To)),
            SmartSearchQueryOperator.Ge => YearRange(RuleValues.Number(rule, rule.Value), QueryLimits.MaxYear),
            SmartSearchQueryOperator.Gt => YearRange(Math.Floor(RuleValues.Number(rule, rule.Value)) + 1, QueryLimits.MaxYear),
            SmartSearchQueryOperator.Le => YearRange(QueryLimits.MinYear, RuleValues.Number(rule, rule.Value)),
            SmartSearchQueryOperator.Lt => YearRange(QueryLimits.MinYear, Math.Ceiling(RuleValues.Number(rule, rule.Value)) - 1),
            SmartSearchQueryOperator.IsNotEmpty => YearRange(QueryLimits.MinYear, QueryLimits.MaxYear),
            _ => null
        };
        return years is null
            ? RuleMapping.Declined("negative operators aren't pushed")
            : RuleMapping.Exact(PushdownSet.Of(PushProperty.Years, new InSetConstraint<int>(years, EqualityComparer<int>.Default, singleValued: true)));
    }

    private static RuleMapping CommunityRating(SmartSearchQueryNode rule, SmartSearchQueryOperator op)
    {
        // The column is REAL holding the float; the evaluator compares in float space, so one float step below the
        // rule's float keeps every match, and the rule is re-checked.
        var lower = op switch
        {
            SmartSearchQueryOperator.Ge or SmartSearchQueryOperator.Gt or SmartSearchQueryOperator.Eq => RuleValues.Number(rule, rule.Value),
            SmartSearchQueryOperator.Between => RuleValues.Number(rule, rule.From),
            _ => (double?)null
        };
        return lower is { } value
            ? RuleMapping.Superset(PushdownSet.Of(PushProperty.MinCommunityRating, new BoundConstraint<double>(MathF.BitDecrement((float)value), lower: true)), "a float step lower, re-checked in float space")
            : RuleMapping.Declined("there is no SQL maximum");
    }

    private static RuleMapping Resolution(SmartSearchQueryNode rule, SmartSearchQueryOperator op)
    {
        if (op != SmartSearchQueryOperator.AnyOf)
        {
            return RuleMapping.Declined("NoneOf isn't pushed");
        }

        var chosen = RuleValues.Strings(rule).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (chosen.Contains("Unknown"))
        {
            return RuleMapping.Declined("Unknown (no width) has no SQL filter");
        }

        var buckets = Buckets.Where(b => chosen.Contains(b.Bucket)).ToList();
        if (buckets.Count == 0)
        {
            return RuleMapping.Declined("no known resolution");
        }

        // The widths from the lowest bucket's start to the highest one's end (gaps included); re-checked.
        var min = buckets.Min(b => b.Min);
        var max = buckets.Any(b => b.Max is null) ? (int?)null : buckets.Max(b => b.Max!.Value);
        var set = PushdownSet.Of(PushProperty.MinWidth, new BoundConstraint<int>(min, lower: true));
        if (max is { } top)
        {
            set = set.And(PushdownSet.Of(PushProperty.MaxWidth, new BoundConstraint<int>(top, lower: false)));
        }

        return RuleMapping.Superset(set, "a width range, re-checked by bucket");
    }

    private RuleMapping Title(SmartSearchQueryNode rule, SmartSearchQueryOperator op)
    {
        if (rule.IncludeOriginalTitle == true)
        {
            return RuleMapping.Declined("SQL's lower() doesn't fold the original title as the evaluator does");
        }

        if (op is not (SmartSearchQueryOperator.Contains or SmartSearchQueryOperator.Eq))
        {
            return RuleMapping.Declined("no SQL filter for this operator");
        }

        if (_facts.CleanForms is not { } forms)
        {
            return RuleMapping.Declined("the clean-form probe hasn't read the clean names");
        }

        var value = RuleValues.Text(rule);
        var folded = RuleValues.Fold(value);
        if (string.IsNullOrWhiteSpace(folded))
        {
            return RuleMapping.Declined("the value has no clean form");
        }

        if (op == SmartSearchQueryOperator.Eq)
        {
            // Core cleans Name itself: CleanName == clean(value), the evaluator's fold (L1946-1957).
            var exactness = CleanFormRisk.ForName(forms.Names, value);
            var set = PushdownSet.Of(PushProperty.Name, new ValueConstraint<string>(value));
            return exactness switch
            {
                PushdownExactness.Exact => RuleMapping.Exact(set),
                PushdownExactness.Superset => RuleMapping.Superset(set, "a stale clean name matches the value"),
                _ => RuleMapping.Declined("a stale clean name would hide a match")
            };
        }

        // Core passes the term unchanged to a case-sensitive CleanName match, so the term is folded here; a term of
        // wildcards alone matches everything.
        if (folded.All(c => c is '%' or '_'))
        {
            return RuleMapping.Declined("a term of wildcards alone matches every item");
        }

        return CleanFormRisk.ForNameContains(forms.Names, folded) == PushdownExactness.Declined
            ? RuleMapping.Declined("a stale clean name would hide a match")
            : RuleMapping.Superset(PushdownSet.Of(PushProperty.NameContains, new ValueConstraint<string>(folded)), "SQL also matches the original title, and LIKE widens % and _");
    }

    private RuleMapping PremiereDate(SmartSearchQueryNode rule, SmartSearchQueryOperator op)
    {
        const string Why = "a day wider each way, re-checked by the calendar rule";
        return op switch
        {
            SmartSearchQueryOperator.After => RuleMapping.Superset(DateBound(PushProperty.MinPremiereDate, LowerEdge(RuleValues.Date(rule, rule.Value), afterTheDay: true), lower: true), Why),
            SmartSearchQueryOperator.Before => RuleMapping.Superset(DateBound(PushProperty.MaxPremiereDate, UpperEdge(RuleValues.Date(rule, rule.Value), beforeTheDay: true), lower: false), Why),
            SmartSearchQueryOperator.Between => RuleMapping.Superset(
                DateBound(PushProperty.MinPremiereDate, LowerEdge(RuleValues.Date(rule, rule.From), afterTheDay: false), lower: true)
                    .And(DateBound(PushProperty.MaxPremiereDate, UpperEdge(RuleValues.Date(rule, rule.To), beforeTheDay: false), lower: false)),
                Why),
            SmartSearchQueryOperator.InLast => RuleMapping.Superset(DateBound(PushProperty.MinPremiereDate, Shift(RuleValues.SpanStart(rule, _environment.NowUtc), -DateSlack), lower: true), Why),
            _ => RuleMapping.Declined("no SQL filter for this operator")
        };
    }

    private RuleMapping DateAdded(SmartSearchQueryNode rule, SmartSearchQueryOperator op)
    {
        const string Why = "a day early, re-checked on the server's day; there is no MaxDateCreated";
        return op switch
        {
            SmartSearchQueryOperator.After => RuleMapping.Superset(DateBound(PushProperty.MinDateCreated, LowerEdge(RuleValues.Date(rule, rule.Value), afterTheDay: true), lower: true), Why),
            SmartSearchQueryOperator.Between => RuleMapping.Superset(DateBound(PushProperty.MinDateCreated, LowerEdge(RuleValues.Date(rule, rule.From), afterTheDay: false), lower: true), Why),
            SmartSearchQueryOperator.InLast => RuleMapping.Superset(DateBound(PushProperty.MinDateCreated, Shift(RuleValues.SpanStart(rule, _environment.NowUtc), -DateSlack), lower: true), Why),
            _ => RuleMapping.Declined("no SQL filter for this operator")
        };
    }

    private RuleMapping Library(SmartSearchQueryNode rule, SmartSearchQueryOperator op)
    {
        // The requested libraries the caller can open, by their physical folders (the evaluator's membership is the same).
        var folders = _scope.Membership.FoldersOf(RuleValues.Ids(rule));
        return op switch
        {
            SmartSearchQueryOperator.AnyOf => RuleMapping.Exact(Ids(PushProperty.TopParentIds, folders, singleValued: true)),
            SmartSearchQueryOperator.NoneOf => RuleMapping.Exact(Ids(PushProperty.TopParentIds, _scope.AllowedFolders.Except(folders), singleValued: true)),
            _ => RuleMapping.Declined("no SQL filter for this operator")
        };
    }

    private static RuleMapping ItemType(SmartSearchQueryNode rule, SmartSearchQueryOperator op)
    {
        var kinds = RuleValues.ItemKinds(rule);
        IEnumerable<BaseItemKind>? set = op switch
        {
            SmartSearchQueryOperator.AnyOf => kinds,
            SmartSearchQueryOperator.NoneOf => QueryLimits.ItemKindOrder.Except(kinds),
            _ => null
        };
        return set is null
            ? RuleMapping.Declined("no SQL filter for this operator")
            : RuleMapping.Exact(PushdownSet.Of(PushProperty.IncludeItemTypes, new InSetConstraint<BaseItemKind>(set, EqualityComparer<BaseItemKind>.Default, singleValued: true)));
    }

    private RuleMapping Values(SmartSearchQueryNode rule, SmartSearchQueryOperator op, PushProperty property, ItemValueType type, bool excludable)
    {
        // IsEmpty and IsNotEmpty have no Values (and no SQL filter); neither has NoneOf of genres.
        IReadOnlyList<string> pushed;
        var exclude = false;
        string? why = null;
        if (op == SmartSearchQueryOperator.AnyOf)
        {
            pushed = RuleValues.Strings(rule);
        }
        else if (op == SmartSearchQueryOperator.AllOf)
        {
            pushed = [RuleValues.Strings(rule)[0]];
            why = "AllOf pushes its first value";
        }
        else if (op == SmartSearchQueryOperator.NoneOf && excludable)
        {
            pushed = RuleValues.Strings(rule);
            exclude = true;
        }
        else
        {
            return RuleMapping.Declined("no SQL filter for this operator");
        }

        var risk = ValueRisk(type, pushed, exclude);
        if (risk.Exactness == PushdownExactness.Declined)
        {
            return risk;
        }

        var set = exclude
            ? PushdownSet.Of(PushProperty.ExcludeTags, new ExcludeSetConstraint(pushed))
            : PushdownSet.Of(property, new InSetConstraint<string>(pushed, FoldComparer.Instance, singleValued: false));
        return why is not null ? RuleMapping.Superset(set, why) : risk.Exactness == PushdownExactness.Superset ? RuleMapping.Superset(set, risk.Reason!) : RuleMapping.Exact(set);
    }

    // Stale clean values and the '|' rows: Exact, Superset (with the reason) or Declined.
    private RuleMapping ValueRisk(ItemValueType type, IReadOnlyList<string> values, bool exclude)
    {
        if (_facts.CleanForms is not { } forms)
        {
            return RuleMapping.Declined("the clean-form probe hasn't read the clean values");
        }

        if (!_pipesRead)
        {
            _pipes = _facts.PipeValues(_cancellationToken);
            _pipesRead = true;
        }

        if (_pipes is not { } pipes)
        {
            return RuleMapping.Declined("the values holding '|' couldn't be read");
        }

        return CleanFormRisk.ForValues(forms.Values.Concat(pipes), type, values, exclude) switch
        {
            PushdownExactness.Exact => RuleMapping.Exact(PushdownSet.None),
            PushdownExactness.Superset => RuleMapping.Superset(PushdownSet.None, "a stale or '|' value row matches in SQL only"),
            _ => RuleMapping.Declined("a stale or '|' value row would hide a match")
        };
    }

    private RuleMapping Studios(SmartSearchQueryNode rule, SmartSearchQueryOperator op)
    {
        if (op != SmartSearchQueryOperator.AnyOf)
        {
            return RuleMapping.Declined("no SQL filter for this operator");
        }

        if (_facts.CleanForms is not { } forms)
        {
            return RuleMapping.Declined("the clean-form probe hasn't read the clean names");
        }

        // StudioIds matches the Studios values whose CleanValue is the Studio item's CleanName: only when every name has a
        // Studio item of the same clean name (never GetStudio, which creates one). With a user, a name is pushed only when an
        // item they can see carries it, decided before anything server-wide is read: a studio that only hidden items carry
        // declines for the same reason as a name nobody has, so the plan tells nothing about it.
        var names = RuleValues.Strings(rule);
        var ids = new List<Guid>();
        var stale = forms.Names.Select(n => n.Id).ToHashSet();
        foreach (var name in names)
        {
            Guid id;
            try
            {
                id = _library.GetStudioId(name);
            }
            catch (ArgumentException)
            {
                return RuleMapping.Declined("a studio name has no studio id");
            }

            if (_scope.User is { } user && !_lookups.HasStudio(user, _scope.AllowedFolders, _scope.Kinds, _scope.IncludeMissing, name, id, _cancellationToken))
            {
                return RuleMapping.Declined("no item the caller can see carries a studio of that name");
            }

            if (_library.GetItemById(id) is not Studio studio || studio.Name is null
                || !string.Equals(RuleValues.Fold(studio.Name), RuleValues.Fold(name), StringComparison.Ordinal) || stale.Contains(id))
            {
                return RuleMapping.Declined("a studio has no Studio item (yet) of that name");
            }

            ids.Add(id);
        }

        var risk = ValueRisk(ItemValueType.Studios, names, exclude: false);
        if (risk.Exactness == PushdownExactness.Declined)
        {
            return risk;
        }

        var set = Ids(PushProperty.StudioIds, ids, singleValued: false);
        return risk.Exactness == PushdownExactness.Superset ? RuleMapping.Superset(set, risk.Reason!) : RuleMapping.Exact(set);
    }

    private RuleMapping UserData(SmartSearchQueryNode rule, FieldDescriptor field, PushProperty property, UserDataFlag flag)
    {
        if (!RuleValues.Bool(rule))
        {
            return RuleMapping.Declined("Is false isn't pushed: SQL drops items without user data");
        }

        if (_scope.User is not { } user)
        {
            return RuleMapping.Declined("there is no user");
        }

        if (!_scope.Kinds.All(field.Kinds.Contains))
        {
            return RuleMapping.Declined("the query's kinds reach beyond the field's");
        }

        if (flag == UserDataFlag.Resumable && _scope.IncludeMissing)
        {
            return RuleMapping.Declined("IsResumable makes core drop missing items");
        }

        return _facts.UserDataAgrees(user.Id, flag, _cancellationToken)
            ? RuleMapping.Superset(Flag(property, true), "SQL reads any of the item's user-data rows, re-checked")
            : RuleMapping.Declined("the user's user-data rows of one item disagree, or couldn't be read");
    }

    private static RuleMapping Provider(SmartSearchQueryNode rule, string why)
    {
        var property = rule.Provider?.Trim().ToUpperInvariant() switch
        {
            "TMDB" => PushProperty.HasTmdbId,
            "IMDB" => PushProperty.HasImdbId,
            "TVDB" => PushProperty.HasTvdbId,
            _ => (PushProperty?)null
        };
        return property is { } p ? RuleMapping.Superset(Flag(p, true), why) : RuleMapping.Declined("no SQL filter for this provider");
    }

    private RuleMapping Series(SmartSearchQueryNode rule, SmartSearchQueryOperator op)
    {
        if (op != SmartSearchQueryOperator.AnyOf)
        {
            return RuleMapping.Declined("NoneOf isn't pushed");
        }

        // An episode's AncestorIds hold its season, its series, its physical folder, the root and every library whose
        // locations hold that folder (BaseItem.GetAncestorIds, LibraryManager.GetCollectionFolders), while the rule is
        // Episode.SeriesId: only for a Series id is it the same filter (a library's id would leak a hidden library's
        // membership through a folder it shares). The ids are resolved in the caller's scope, and every id that isn't a
        // series the caller can see there (no item, another kind, a series in a library they can't open or that their
        // access hides) is treated alike: the rule stays in memory, so the plan tells nothing about whether an id exists.
        return SeriesIds(RuleValues.ReferenceIds(rule)) is { } series
            ? RuleMapping.Exact(Ids(PushProperty.AncestorIds, series, singleValued: false))
            : RuleMapping.Declined("an id that isn't a series in the scope");
    }

    // The ids, when every one is a Series item the caller can see in the plan's libraries (with their access, as the
    // candidates are loaded); null otherwise.
    private List<Guid>? SeriesIds(IEnumerable<Guid> ids)
    {
        var distinct = ids.Distinct().ToList();
        if (distinct.Count == 0)
        {
            return [];
        }

        if (distinct.Contains(Guid.Empty) || _scope.AllowedFolders.Count == 0)
        {
            return null;
        }

        _cancellationToken.ThrowIfCancellationRequested();
        var query = QueryPlanner.CreateAccessQuery(_scope.User, PlanPurpose.EvaluateAll);
        query.ItemIds = [.. distinct];
        query.IncludeItemTypes = [BaseItemKind.Series];
        query.TopParentIds = [.. _scope.AllowedFolders];
        query.DtoOptions = new MediaBrowser.Controller.Dto.DtoOptions(false) { EnableImages = false, EnableUserData = false };
        var visible = _library.GetItemList(query).Where(i => i is Series && !Kit.Library.LibraryAccess.IsOwned(i)).Select(i => i.Id).ToHashSet();
        return distinct.All(visible.Contains) ? distinct : null;
    }

    private RuleMapping SeriesName(SmartSearchQueryNode rule)
    {
        // The SeriesResolver: the series names episodes store (they can lag a rename, so the series' own names
        // won't do), matched by the rule itself, give the series ids. With a user, only the episodes the user can see are
        // read (their access query and IsVisible), so a name that hidden episodes alone carry matches nothing, exactly as
        // a name nobody has: the plan never tells whether a hidden series exists.
        var pairs = _scope.User is { } user
            ? _lookups.EpisodeSeries(user, _scope.AllowedFolders, _scope.IncludeMissing, _cancellationToken)
            : _facts.EpisodeSeries(_scope.AllowedFolders, _cancellationToken);
        if (pairs is null)
        {
            return RuleMapping.Declined("the episodes' series names couldn't be read");
        }

        var test = _registry.EvaluatorOf(RuleTree.FieldOf(rule, _registry)).Compile(rule, _environment);
        var matched = pairs.Where(p => test(new EvaluationRow(new Episode { SeriesName = p.SeriesName }))).Select(p => p.SeriesId).Distinct().ToList();
        if (matched.Contains(Guid.Empty))
        {
            return RuleMapping.Declined("a matching episode has no series id");
        }

        // As for a Series rule, AncestorIds is a series filter only for Series ids.
        if (SeriesIds(matched) is not { } series || series.Count != matched.Count)
        {
            return RuleMapping.Declined("a matching episode's series id isn't a Series item");
        }

        return RuleMapping.Superset(Ids(PushProperty.AncestorIds, series, singleValued: false), "the series of the matching names; the episode's name is re-checked");
    }
}
