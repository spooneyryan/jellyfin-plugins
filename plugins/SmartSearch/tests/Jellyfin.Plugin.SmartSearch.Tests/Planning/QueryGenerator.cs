using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.SmartSearch.Query.Fields;

namespace Jellyfin.Plugin.SmartSearch.Tests.Planning;

/// <summary>
/// The differential test's query generator: random trees (All, Any, negated groups, up to the depth limit) of rules on
/// every field with every operator, values drawn from the library's pools (changed in case and diacritics, cut to
/// substrings, wildcard terms), random scopes (kinds, BoxSet alone, libraries the user can or can't open, missing items),
/// sorts and limits. A Series rule names series, and sometimes ids that are an episode's other ancestors (its season,
/// folder, the root, a library over its folder, one the user can't open), which 10.11.5's <c>AncestorIds</c> holds too.
/// It writes JSON as a client would; the validator decides which queries run.
/// </summary>
internal sealed class QueryGenerator
{
    private static readonly string[] Kinds = ["Movie", "Series", "Episode", "MusicVideo", "Video", "BoxSet"];

    private static readonly string[] TextOps = ["Contains", "NotContains", "Eq", "Ne", "StartsWith", "EndsWith", "IsEmpty", "IsNotEmpty"];

    private static readonly string[] NumberOps = ["Eq", "Ne", "Lt", "Le", "Gt", "Ge", "Between", "IsEmpty", "IsNotEmpty"];

    private static readonly string[] DateOps = ["Before", "After", "Between", "InLast", "NotInLast", "IsEmpty", "IsNotEmpty"];

    private static readonly string[] ListOps = ["AnyOf", "AllOf", "NoneOf", "IsEmpty", "IsNotEmpty"];

    private static readonly string[] Units = ["Days", "Weeks", "Months", "Years"];

    private static readonly string[] Wildcards = ["%", "_", "%_", "100%", "snake_case", "[rec]", "up^down", "[", "^", "%amel", "e%"];

    private readonly Random _rng;
    private readonly IReadOnlyList<string> _names;
    private readonly IReadOnlyList<string> _genres;
    private readonly IReadOnlyList<string> _tags;
    private readonly IReadOnlyList<string> _studios;
    private readonly IReadOnlyList<string> _ratings;
    private readonly IReadOnlyList<Guid> _libraries;
    private readonly IReadOnlyList<(Guid Id, string Name)> _series;
    private readonly IReadOnlyList<(Guid Id, string Name)> _notSeries;

    public QueryGenerator(Random rng, IReadOnlyList<string> names, IReadOnlyList<string> genres, IReadOnlyList<string> tags, IReadOnlyList<string> studios, IReadOnlyList<string> ratings, IReadOnlyList<Guid> libraries, IReadOnlyList<(Guid Id, string Name)> series, IReadOnlyList<(Guid Id, string Name)>? notSeries = null)
    {
        _notSeries = notSeries ?? [];
        _rng = rng;
        _names = names;
        _genres = genres;
        _tags = tags;
        _studios = studios;
        _ratings = ratings;
        _libraries = libraries;
        _series = series;
    }

    /// <summary>Gets how many Series rules this generator wrote with an id that isn't a series.</summary>
    public int NotSeriesRules { get; private set; }

    public string Next()
    {
        // A fifth are simple queries of fields that push exactly, so the Fast path gets its share; a tenth search box
        // sets alone (core's /Items filters and counts those in memory); a twentieth search episodes by an id that is
        // another of their ancestors (a series' AncestorIds push matched those).
        var simple = _rng.Next(5) == 0;
        var ancestor = _notSeries.Count > 0 && _rng.Next(20) == 0;
        var kinds = ancestor ? ["Episode"] : _rng.Next(10) switch { < 3 => Kinds.ToList(), 3 => ["BoxSet"], _ => Kinds.Where(_ => _rng.Next(2) == 0).ToList() };
        if (kinds.Count == 0)
        {
            kinds.Add(Pick(Kinds));
        }

        var scope = new List<string> { "\"ItemKinds\":[" + string.Join(",", kinds.Select(Str)) + "]" };
        if (_rng.Next(5) < 2)
        {
            var ids = Enumerable.Range(0, 1 + _rng.Next(2)).Select(_ => _rng.Next(12) == 0 ? Guid.NewGuid() : Pick(_libraries)).Distinct();
            scope.Add("\"LibraryIds\":[" + string.Join(",", ids.Select(id => Str(id.ToString(_rng.Next(2) == 0 ? "N" : "D", CultureInfo.InvariantCulture)))) + "]");
        }

        if (_rng.Next(5) == 0)
        {
            scope.Add("\"IncludeMissing\":true");
        }

        var children = ancestor
            ? [SeriesRule("AnyOf", notSeries: true)]
            : simple
            ? Enumerable.Range(0, 1 + _rng.Next(2)).Select(_ => ExactRule()).ToList()
            : Enumerable.Range(0, 1 + _rng.Next(4)).Select(_ => Node(1)).ToList();
        var root = "{" + (!simple && _rng.Next(6) == 0 ? "\"Match\":\"Any\"," : string.Empty) + (!simple && _rng.Next(15) == 0 ? "\"Negate\":true," : string.Empty) + "\"Children\":[" + string.Join(",", children) + "]}";
        var json = "{\"Scope\":{" + string.Join(",", scope) + "},\"Where\":" + root;
        if (_rng.Next(3) == 0)
        {
            var keys = Enumerable.Range(0, 1 + _rng.Next(2)).Select(_ => Pick(simple ? ["SortName", "ProductionYear", "CommunityRating", "DatePlayed", "PlayCount"] : FieldRegistry.SortKeyList)).Distinct();
            json += ",\"Sort\":[" + string.Join(",", keys.Select(k => "{\"Key\":" + Str(k) + ",\"Order\":" + Str(_rng.Next(2) == 0 ? "Ascending" : "Descending") + "}")) + "]";
        }

        var limit = _rng.Next(8) switch { 0 => "10", 1 => "200", 2 when !simple => "201", _ => null };
        if (limit is not null)
        {
            json += ",\"Limit\":" + limit;
        }

        return json + "}";
    }

    private static string Str(string value) => JsonSerializer.Serialize(value);

    private static string Num(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private T Pick<T>(IReadOnlyList<T> pool) => pool[_rng.Next(pool.Count)];

    private string Node(int depth)
    {
        if (depth < 3 && _rng.Next(5) == 0)
        {
            var children = Enumerable.Range(0, 1 + _rng.Next(3)).Select(_ => Node(depth + 1));
            return "{\"Match\":" + Str(_rng.Next(2) == 0 ? "Any" : "All") + (_rng.Next(5) == 0 ? ",\"Negate\":true" : string.Empty) + ",\"Children\":[" + string.Join(",", children) + "]}";
        }

        return Rule();
    }

    // A term a user might type: a whole name, a piece of one, in another case, without its diacritics, or wildcards.
    private string Term()
    {
        var name = Pick(_names).Trim();
        if (name.Length == 0 || _rng.Next(8) == 0)
        {
            return Pick(Wildcards);
        }

        var term = _rng.Next(3) switch
        {
            0 => name,
            1 => name[..Math.Max(1, _rng.Next(1, name.Length + 1))],
            _ => name[_rng.Next(name.Length)..]
        };
        term = _rng.Next(4) switch
        {
            0 => term.ToUpperInvariant(),
            1 => term.ToLowerInvariant(),
            2 => SyntheticLibrary.OldClean(term),
            _ => term
        };
        return term.Trim().Length == 0 ? "a" : term;
    }

    private string Values(IReadOnlyList<string> pool)
    {
        var values = Enumerable.Range(0, 1 + _rng.Next(3)).Select(_ => Pick(pool).Trim()).Where(v => v.Length > 0).Select(v => _rng.Next(4) == 0 ? v.ToUpperInvariant() : v).Distinct(StringComparer.Ordinal).ToList();
        return "[" + string.Join(",", (values.Count == 0 ? ["Drama"] : values).Select(Str)) + "]";
    }

    private string Day(int shift = 0) => Pick(SyntheticLibrary.Days).AddDays(shift + _rng.Next(-1, 2)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private string DateValue() => _rng.Next(3) switch
    {
        0 => Day(),
        1 => Day() + "T" + Pick(new[] { "00:00:00Z", "12:00:00Z", "05:00:00Z", "14:00:00Z", "23:59:59Z" }),
        _ => Day() + "T00:00:00" + Pick(new[] { "+10:00", "-05:00", "+00:00" })
    };

    private string Text(string field, IReadOnlyList<string> ops)
    {
        var op = Pick(ops);
        var rule = "{\"Field\":" + Str(field) + ",\"Op\":" + Str(op);
        if (op is not ("IsEmpty" or "IsNotEmpty"))
        {
            rule += ",\"Value\":" + Str(Term());
        }

        if (field == "Title" && _rng.Next(4) == 0)
        {
            rule += ",\"IncludeOriginalTitle\":" + (_rng.Next(2) == 0 ? "true" : "false");
        }

        return rule + "}";
    }

    private string Number(string field, double min, double max, bool integer, bool anyOf = false)
    {
        double Value()
        {
            var v = min + (_rng.NextDouble() * (max - min));
            if (field == "CommunityRating" && _rng.Next(2) == 0)
            {
                v = Pick(new[] { 7.1, 6.9, 5.5, 0, 10, 8.25 });
            }
            else if (field == "Year" && _rng.Next(2) == 0)
            {
                v = Pick(new[] { 1800, 1801, 1999, 2000, 2005, 2099, 2100 });
            }

            return integer ? Math.Round(v) : Math.Round(v, 2);
        }

        var op = anyOf && _rng.Next(6) == 0 ? "AnyOf" : Pick(NumberOps);
        var rule = "{\"Field\":" + Str(field) + ",\"Op\":" + Str(op);
        switch (op)
        {
            case "AnyOf":
                rule += ",\"Values\":[" + string.Join(",", Enumerable.Range(0, 1 + _rng.Next(3)).Select(_ => Num(Value())).Distinct()) + "]";
                break;
            case "Between":
                var a = Value();
                var b = field == "Year" ? Math.Min(max, a + _rng.Next(0, 30)) : Value();
                rule += ",\"From\":" + Num(Math.Min(a, b)) + ",\"To\":" + Num(Math.Max(a, b));
                break;
            case "IsEmpty" or "IsNotEmpty":
                break;
            default:
                rule += ",\"Value\":" + Num(Value());
                break;
        }

        return rule + "}";
    }

    private string Date(string field)
    {
        var op = Pick(DateOps);
        var rule = "{\"Field\":" + Str(field) + ",\"Op\":" + Str(op);
        switch (op)
        {
            case "Before" or "After":
                rule += ",\"Value\":" + Str(DateValue());
                break;
            case "Between":
                var from = Day();
                rule += ",\"From\":" + Str(from) + ",\"To\":" + Str(DateOnly.Parse(from, CultureInfo.InvariantCulture).AddDays(_rng.Next(0, 60)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                break;
            case "InLast" or "NotInLast":
                var unit = Pick(Units);
                var amount = unit switch { "Days" => Pick(new[] { 1, 2, 30, 31, 400 }), "Weeks" => Pick(new[] { 1, 4 }), "Months" => Pick(new[] { 1, 10 }), _ => 1 };
                rule += ",\"Amount\":" + amount + ",\"Unit\":" + Str(unit);
                break;
        }

        return rule + "}";
    }

    private string List(string field, IReadOnlyList<string> pool)
    {
        var op = Pick(ListOps);
        return "{\"Field\":" + Str(field) + ",\"Op\":" + Str(op) + (op is "IsEmpty" or "IsNotEmpty" ? string.Empty : ",\"Values\":" + Values(pool)) + "}";
    }

    private string Choice(string field, IReadOnlyList<string> choices)
        => "{\"Field\":" + Str(field) + ",\"Op\":" + Str(_rng.Next(3) == 0 ? "NoneOf" : "AnyOf") + ",\"Values\":" + Values(choices) + "}";

    private string Bool(string field, string? provider = null)
        => "{\"Field\":" + Str(field) + ",\"Op\":\"Is\",\"Value\":" + (_rng.Next(2) == 0 ? "true" : "false") + (provider is null ? string.Empty : ",\"Provider\":" + Str(provider)) + "}";

    // Series values: series, and (a third of the time, or always) also an id that is another ancestor of episodes.
    private string SeriesRule(string op, bool notSeries = false)
    {
        var values = Enumerable.Range(0, 1 + _rng.Next(2)).Select(_ => Pick(_series)).ToList();
        if (_notSeries.Count > 0 && (notSeries || _rng.Next(3) == 0))
        {
            values.Insert(_rng.Next(values.Count + 1), Pick(_notSeries));
            if (_rng.Next(2) == 0)
            {
                values.RemoveAll(v => _series.Contains(v));
                values.Add(Pick(_notSeries));
            }

            NotSeriesRules++;
        }

        return "{\"Field\":\"Series\",\"Op\":" + Str(op) + ",\"Values\":[" + string.Join(",", values.Distinct().Select(s => "{\"Id\":" + Str(s.Id.ToString("N")) + ",\"Name\":" + Str(s.Name) + "}")) + "]}";
    }

    // A rule of a field that can push exactly (it still may not, for the kinds or the clean forms).
    private string ExactRule() => _rng.Next(10) switch
    {
        9 when _series.Count > 0 => SeriesRule("AnyOf"),
        0 or 1 => Number("Year", 1800, 2100, integer: true, anyOf: true),
        2 => Choice("ItemType", Kinds),
        3 => "{\"Field\":\"Library\",\"Op\":" + Str(_rng.Next(3) == 0 ? "NoneOf" : "AnyOf") + ",\"Values\":[" + Str(Pick(_libraries).ToString("N")) + "]}",
        4 => "{\"Field\":\"OfficialRating\",\"Op\":\"AnyOf\",\"Values\":" + Values(_ratings) + "}",
        5 => Bool("Locked"),
        6 => "{\"Field\":\"Genres\",\"Op\":\"AnyOf\",\"Values\":" + Values(_genres) + "}",
        7 => "{\"Field\":\"Studios\",\"Op\":\"AnyOf\",\"Values\":" + Values(_studios) + "}",
        _ => "{\"Field\":\"Title\",\"Op\":\"Eq\",\"Value\":" + Str(Pick(_names)) + "}"
    };

    private string Rule()
    {
        var provider = Pick(new[] { "Tmdb", "imdb", "TVDB", "Imdb" });
        return _rng.Next(31) switch
        {
            0 or 1 or 2 => Text("Title", TextOps),
            3 => Text("OriginalTitle", TextOps),
            4 => Text("Overview", ["Contains", "NotContains", "IsEmpty", "IsNotEmpty"]),
            5 => Text("SeriesName", TextOps),
            6 => SeriesRule(_rng.Next(3) == 0 ? "NoneOf" : "AnyOf"),
            7 or 8 => Number("Year", 1800, 2100, integer: true, anyOf: true),
            9 => Date("PremiereDate"),
            10 => Date("DateAdded"),
            11 => "{\"Field\":\"Library\",\"Op\":" + Str(_rng.Next(3) == 0 ? "NoneOf" : "AnyOf") + ",\"Values\":[" + string.Join(",", Enumerable.Range(0, 1 + _rng.Next(2)).Select(_ => Str(Pick(_libraries).ToString("N")))) + "]}",
            12 => Choice("ItemType", Kinds),
            13 => List("Genres", _genres),
            14 => List("Tags", _tags),
            15 => List("Studios", _studios),
            16 => _rng.Next(4) == 0
                ? "{\"Field\":\"OfficialRating\",\"Op\":" + Str(_rng.Next(2) == 0 ? "Le" : "Ge") + ",\"Value\":" + Str(Pick(new[] { "PG", "PG-13", "R" })) + "}"
                : Choice("OfficialRating", _ratings),
            17 => Choice("SeriesStatus", ["Continuing", "Ended", "Unreleased"]),
            18 => Number("CommunityRating", 0, 10, integer: false),
            19 => Number("CriticRating", 0, 100, integer: false),
            20 => Number("Runtime", 0, 180, integer: true),
            21 => Choice("Resolution", ["SD", "720p", "1080p", "1440p", "2160p", "Unknown"]),
            22 => Choice("Container", ["mkv", "mp4", "MKV", "avi", "mov", "3gp"]),
            23 => Choice("VideoType", ["VideoFile", "Iso", "Dvd", "BluRay"]),
            24 => Bool("Played"),
            25 => Bool("Favorite"),
            26 => Bool("InProgress"),
            27 => _rng.Next(2) == 0 ? Number("PlayCount", 0, 4, integer: true) : Date("LastPlayed"),
            28 => Bool("HasProviderId", provider),
            29 => "{\"Field\":\"ProviderId\",\"Op\":\"Eq\",\"Value\":" + Str(Pick(new[] { "603", "tt0133093", "TT0133093", "81189", "1399", " x " })) + ",\"Provider\":" + Str(provider) + "}",
            _ => _rng.Next(2) == 0 ? Bool("Locked") : Bool("Is3D")
        };
    }
}
