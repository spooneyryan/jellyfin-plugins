using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.SmartSearch.Api.Models;
using Jellyfin.Plugin.SmartSearch.Query.Evaluation;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.SmartSearch.Query.Fields;

/// <summary>
/// The single source of truth for fields: every field a query
/// may name, with its descriptor (what the validator, the canonicalizer and <c>GET /SmartSearch/Fields</c> read) and its
/// in-memory evaluator, and the sort keys. The first set of fields; the later ones (<c>People</c>, <c>InCollection</c>, the
/// rating facade's and the stream facts' fields) arrive in later versions. The pushdown mappers are the planner's.
/// </summary>
/// <remarks>
/// <see cref="FieldDescriptor.Cost"/> is where the field is evaluated in memory: <c>Series</c> ("Sql/Row" in the design's field table) and
/// <c>SeriesName</c> ("Set/Row") are <see cref="FieldCost.Row"/>, because their resolver or pushdown only narrows the
/// candidates and the rule is still checked on each item.
/// </remarks>
internal sealed class FieldRegistry : FieldCatalog
{
    /// <summary>
    /// The registry's version: changes whenever a field, operator, option or sort key is
    /// added, removed or changed, so the rule builder can drop what it cached.
    /// </summary>
    public const int Version = 2;

    private static readonly BaseItemKind[] All = [BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Episode, BaseItemKind.MusicVideo, BaseItemKind.Video, BaseItemKind.BoxSet];
    private static readonly BaseItemKind[] Msev = [BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Episode, BaseItemKind.MusicVideo];
    private static readonly BaseItemKind[] Msevh = [BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Episode, BaseItemKind.MusicVideo, BaseItemKind.Video];
    private static readonly BaseItemKind[] Mevh = [BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.MusicVideo, BaseItemKind.Video];
    private static readonly BaseItemKind[] Ms = [BaseItemKind.Movie, BaseItemKind.Series];
    private static readonly string[] Providers = ["Tmdb", "Imdb", "Tvdb"];

    private static readonly (FieldDescriptor Field, IRowEvaluator Evaluator)[] Entries = BuildEntries();

    private readonly Dictionary<string, IRowEvaluator> _evaluators;

    /// <summary>
    /// Initializes a new instance of the <see cref="FieldRegistry"/> class.
    /// </summary>
    public FieldRegistry()
        : this(Entries)
    {
    }

    private FieldRegistry((FieldDescriptor Field, IRowEvaluator Evaluator)[] entries)
        : base(entries.Select(e => e.Field), SortKeyList)
    {
        _evaluators = entries.ToDictionary(
            e => e.Field.Id,
            e => e.Field.Kinds.Count < QueryLimits.ItemKinds.Count ? new KindScopedEvaluator(e.Field.Kinds, e.Evaluator) : (IRowEvaluator)e.Evaluator,
            StringComparer.Ordinal);
    }

    /// <summary>
    /// Gets the sort keys: core's <c>ItemSortBy</c> keys, then the plugin keys. <c>MemberRating</c>
    /// arrives with the rating facade.
    /// </summary>
    public static IReadOnlyList<string> SortKeyList { get; } =
    [
        "SortName", "ProductionYear", "PremiereDate", "DateCreated", "CommunityRating", "CriticRating", "Runtime", "OfficialRating",
        "VideoBitRate", "DatePlayed", "PlayCount", "Random", "FileSize", "Resolution", "SeriesName"
    ];

    /// <summary>
    /// Creates a registry with more fields than the built-in ones: the tests' facts fields, until real ones arrive (the engine's
    /// three-valued evaluation and phase B).
    /// </summary>
    /// <param name="extra">The extra fields and their evaluators.</param>
    /// <returns>The registry.</returns>
    public static FieldRegistry WithFields(IEnumerable<(FieldDescriptor Field, IRowEvaluator Evaluator)> extra)
    {
        ArgumentNullException.ThrowIfNull(extra);
        return new FieldRegistry([.. Entries, .. extra]);
    }

    /// <summary>
    /// Gets a field's evaluator: false on every row whose kind the field doesn't apply to (<see cref="KindScopedEvaluator"/>).
    /// </summary>
    /// <param name="field">The field (one of <see cref="FieldCatalog.Fields"/>).</param>
    /// <returns>The evaluator.</returns>
    public IRowEvaluator EvaluatorOf(FieldDescriptor field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return _evaluators[field.Id];
    }

    private static (FieldDescriptor, IRowEvaluator)[] BuildEntries() =>
    [
        // Text.
        (new FieldDescriptor
        {
            Id = "Title", Label = "Title", Group = "Text", Type = FieldValueType.Text, Kinds = All, Operators = QueryOperators.Text,
            Options = [new FieldOptionSpec(FieldOption.IncludeOriginalTitle, false, null)],
            Note = "Ignores case and accents. With \"Include original title\" the original title matches too."
        }, new TextEvaluator(_ => row => row.Item.Name, TextComparison.Folded, rule => rule.IncludeOriginalTitle == true ? row => row.Item.OriginalTitle : null)),
        (new FieldDescriptor
        {
            Id = "OriginalTitle", Label = "Original title", Group = "Text", Type = FieldValueType.Text, Kinds = Msev, Operators = QueryOperators.Text,
            Note = "Often empty for items that aren't identified."
        }, TextEvaluator.Of(row => row.Item.OriginalTitle)),
        (new FieldDescriptor
        {
            Id = "Overview", Label = "Overview", Group = "Text", Type = FieldValueType.Text, Kinds = All,
            Operators = [SmartSearchQueryOperator.Contains, SmartSearchQueryOperator.NotContains, SmartSearchQueryOperator.IsEmpty, SmartSearchQueryOperator.IsNotEmpty]
        }, TextEvaluator.Of(row => row.Item.Overview)),
        (new FieldDescriptor
        {
            Id = "SeriesName", Label = "Series name", Group = "Text", Type = FieldValueType.Text, Kinds = [BaseItemKind.Episode], Operators = QueryOperators.Text,
            Note = "The show's name, as text. To pick shows, use Series."
        }, TextEvaluator.Of(row => (row.Item as Episode)?.SeriesName)),
        (new FieldDescriptor
        {
            Id = "Series", Label = "Series", Group = "Scope", Type = FieldValueType.Choice, Kinds = [BaseItemKind.Episode], Operators = QueryOperators.Choice,
            ElementKind = FieldElementKind.Reference, ValueSource = "/SmartSearch/Series",
            Note = "Episodes of the chosen shows."
        }, IdChoiceEvaluator.OfReferences(row => (row.Item as Episode)?.SeriesId ?? Guid.Empty)),
        (new FieldDescriptor
        {
            Id = "Path", Label = "Path", Group = "Text", Type = FieldValueType.Text, Kinds = All, AdminOnly = true,
            Operators = [SmartSearchQueryOperator.Contains, SmartSearchQueryOperator.NotContains, SmartSearchQueryOperator.StartsWith, SmartSearchQueryOperator.Matches],
            Note = "Administrators only; queries that use it can't be shared."
        }, TextEvaluator.Of(row => row.Item.Path, TextComparison.OrdinalIgnoreCase)),

        // Dates.
        (new FieldDescriptor
        {
            Id = "Year", Label = "Year", Group = "Dates", Type = FieldValueType.Number, Kinds = Msevh, Operators = [.. QueryOperators.Number, SmartSearchQueryOperator.AnyOf],
            ElementKind = FieldElementKind.Integer, Min = QueryLimits.MinYear, Max = QueryLimits.MaxYear, Integer = true, MaxSpan = QueryLimits.MaxYearSpan,
            Note = "For a series, the year it first aired."
        }, new NumberEvaluator(row => ItemFacts.Year(row.Item))),
        (new FieldDescriptor
        {
            Id = "PremiereDate", Label = "Premiere date", Group = "Dates", Type = FieldValueType.Date, Kinds = Msevh, Operators = QueryOperators.Date,
            Note = "A date without a time is that whole day."
        }, new DateEvaluator(row => row.Item.PremiereDate, calendarDate: true)),
        (new FieldDescriptor
        {
            Id = "DateAdded", Label = "Date added", Group = "Dates", Type = FieldValueType.Date, Kinds = All, Operators = QueryOperators.Date
        }, new DateEvaluator(row => row.Item.DateCreated)),

        // Scope.
        (new FieldDescriptor
        {
            Id = "Library", Label = "Library", Group = "Scope", Type = FieldValueType.Choice, Kinds = All, Operators = QueryOperators.Choice,
            ElementKind = FieldElementKind.Guid, ValueSource = "/SmartSearch/Libraries", Cost = FieldCost.Set,
            Note = "The scope bar picks libraries; this rule is for OR and NOT."
        }, IdChoiceEvaluator.OfLibraries()),
        (new FieldDescriptor
        {
            Id = "ItemType", Label = "Item type", Group = "Scope", Type = FieldValueType.Choice, Kinds = All, Operators = QueryOperators.Choice,
            ElementKind = FieldElementKind.ItemKind, Choices = [.. All.Select(k => k.ToString())]
        }, IdChoiceEvaluator.OfItemKinds()),

        // Metadata.
        (new FieldDescriptor
        {
            Id = "Genres", Label = "Genres", Group = "Metadata", Type = FieldValueType.List, Kinds = All, Operators = QueryOperators.List,
            ElementKind = FieldElementKind.String, ValueSource = "/SmartSearch/Values/Genres"
        }, new ListEvaluator(row => row.Item.Genres)),
        (new FieldDescriptor
        {
            Id = "Tags", Label = "Tags", Group = "Metadata", Type = FieldValueType.List, Kinds = All, Operators = QueryOperators.List,
            ElementKind = FieldElementKind.String, ValueSource = "/SmartSearch/Values/Tags"
        }, new ListEvaluator(row => row.Item.Tags)),
        (new FieldDescriptor
        {
            Id = "Studios", Label = "Studios", Group = "Metadata", Type = FieldValueType.List, Kinds = Msev, Operators = QueryOperators.List,
            ElementKind = FieldElementKind.String, ValueSource = "/SmartSearch/Values/Studios"
        }, new ListEvaluator(row => row.Item.Studios)),
        (new FieldDescriptor
        {
            Id = "OfficialRating", Label = "Parental rating", Group = "Metadata", Type = FieldValueType.Choice, Kinds = All,
            Operators = [SmartSearchQueryOperator.AnyOf, SmartSearchQueryOperator.NoneOf, SmartSearchQueryOperator.Le, SmartSearchQueryOperator.Ge],
            ElementKind = FieldElementKind.String, ValueSource = "/SmartSearch/Values/OfficialRating",
            Note = "At most and at least compare the ratings' ages; unrated items never match them."
        }, new ChoiceEvaluator(row => row.Item.OfficialRating, StringComparer.Ordinal, byRatingScore: true)),
        (new FieldDescriptor
        {
            Id = "SeriesStatus", Label = "Series status", Group = "Metadata", Type = FieldValueType.Choice, Kinds = [BaseItemKind.Series], Operators = QueryOperators.Choice,
            ElementKind = FieldElementKind.String, Choices = ["Continuing", "Ended", "Unreleased"]
        }, new ChoiceEvaluator(row => (row.Item as Series)?.Status?.ToString(), StringComparer.OrdinalIgnoreCase)),

        // Ratings.
        (new FieldDescriptor
        {
            Id = "CommunityRating", Label = "Community rating", Group = "Ratings", Type = FieldValueType.Number, Kinds = All, Operators = QueryOperators.Number,

            // A stored 0 is a rating, as in the server's own filter (MinCommunityRating 0 returns it), although item
            // pages show no rating for it; so "is empty" doesn't match it and "at least 0" does.
            Min = 0, Max = 10, Note = "A rating of 0 counts as a rating, as in the server's filters, though item pages show none."
        }, new NumberEvaluator(row => ItemFacts.Rating(row.Item.CommunityRating), ItemFacts.SinglePrecision)),
        (new FieldDescriptor
        {
            Id = "CriticRating", Label = "Critic rating", Group = "Ratings", Type = FieldValueType.Number, Kinds = Ms, Operators = QueryOperators.Number,
            Min = 0, Max = 100
        }, new NumberEvaluator(row => ItemFacts.Rating(row.Item.CriticRating), ItemFacts.SinglePrecision)),

        // Media (Row fields; the stream facts' fields come later).
        (new FieldDescriptor
        {
            Id = "Runtime", Label = "Runtime (minutes)", Group = "Media", Type = FieldValueType.Number, Kinds = Mevh, Operators = QueryOperators.Number,
            Min = 0, Integer = true, Note = "Rounded to whole minutes."
        }, new NumberEvaluator(row => ItemFacts.RuntimeMinutes(row.Item))),
        (new FieldDescriptor
        {
            Id = "Resolution", Label = "Resolution", Group = "Media", Type = FieldValueType.Choice, Kinds = Mevh, Operators = QueryOperators.Choice,
            ElementKind = FieldElementKind.String, Choices = ItemFacts.Resolutions,
            Note = "From the video's width, so 1920×800 is 1080p."
        }, new ChoiceEvaluator(row => ItemFacts.Resolution(row.Item.Width), StringComparer.OrdinalIgnoreCase)),
        (new FieldDescriptor
        {
            Id = "Container", Label = "Container", Group = "Media", Type = FieldValueType.Choice, Kinds = Mevh, Operators = QueryOperators.Choice,
            ElementKind = FieldElementKind.String, ValueSource = "/SmartSearch/Values/Container"
        }, new ChoiceEvaluator(row => row.Item.Container, StringComparer.OrdinalIgnoreCase, commaSeparated: true)),
        (new FieldDescriptor
        {
            Id = "VideoType", Label = "Video type", Group = "Media", Type = FieldValueType.Choice, Kinds = Mevh, Operators = QueryOperators.Choice,
            ElementKind = FieldElementKind.String, Choices = ["VideoFile", "Iso", "Dvd", "BluRay"]
        }, new ChoiceEvaluator(row => (row.Item as Video)?.VideoType.ToString(), StringComparer.OrdinalIgnoreCase)),
        (new FieldDescriptor
        {
            Id = "Is3D", Label = "3D", Group = "Media", Type = FieldValueType.Bool, Kinds = Mevh, Operators = QueryOperators.Bool
        }, BoolEvaluator.Of(row => row.Item is Video { Video3DFormat: not null })),
        (new FieldDescriptor
        {
            Id = "FileSize", Label = "File size (GB)", Group = "Media", Type = FieldValueType.Number, Kinds = Mevh, Operators = QueryOperators.Number,
            Min = 0, Note = "The main file only; 1 GB is 1024³ bytes."
        }, new NumberEvaluator(row => ItemFacts.FileSizeGb(row.Item))),
        (new FieldDescriptor
        {
            Id = "Bitrate", Label = "Bitrate (Mbps)", Group = "Media", Type = FieldValueType.Number, Kinds = Mevh, Operators = QueryOperators.Number,
            Min = 0
        }, new NumberEvaluator(row => ItemFacts.BitrateMbps(row.Item))),

        // Watch state (the user's data; no row reads as unplayed, not a favourite, not in progress).
        (new FieldDescriptor
        {
            Id = "Played", Label = "Played", Group = "Watch state", Type = FieldValueType.Bool, Kinds = Mevh, Operators = QueryOperators.Bool,
            PerUser = true, Cost = FieldCost.User
        }, BoolEvaluator.Of(row => row.UserData?.Played ?? false)),
        (new FieldDescriptor
        {
            Id = "Favorite", Label = "Favorite", Group = "Watch state", Type = FieldValueType.Bool, Kinds = All, Operators = QueryOperators.Bool,
            PerUser = true, Cost = FieldCost.User
        }, BoolEvaluator.Of(row => row.UserData?.IsFavorite ?? false)),
        (new FieldDescriptor
        {
            Id = "InProgress", Label = "In progress", Group = "Watch state", Type = FieldValueType.Bool, Kinds = Mevh, Operators = QueryOperators.Bool,
            PerUser = true, Cost = FieldCost.User
        }, BoolEvaluator.Of(row => (row.UserData?.PlaybackPositionTicks ?? 0) > 0)),
        (new FieldDescriptor
        {
            Id = "PlayCount", Label = "Play count", Group = "Watch state", Type = FieldValueType.Number, Kinds = Mevh, Operators = QueryOperators.Number,
            Min = 0, Integer = true, PerUser = true, Cost = FieldCost.User
        }, new NumberEvaluator(row => row.UserData?.PlayCount ?? 0)),
        (new FieldDescriptor
        {
            Id = "LastPlayed", Label = "Last played", Group = "Watch state", Type = FieldValueType.Date, Kinds = Mevh, Operators = QueryOperators.Date,
            PerUser = true, Cost = FieldCost.User, Note = "Items never played have no date: \"not in the last …\" includes them."
        }, new DateEvaluator(row => row.UserData?.LastPlayedDate)),

        // Identity.
        (new FieldDescriptor
        {
            Id = "HasProviderId", Label = "Has provider id", Group = "Identity", Type = FieldValueType.Bool, Kinds = Msev, Operators = QueryOperators.Bool,
            Options = [new FieldOptionSpec(FieldOption.Provider, true, Providers)], NeedsProviderIds = true
        }, new BoolEvaluator(rule => row => !string.IsNullOrWhiteSpace(ProviderIdOf(row.Item, rule.Provider)))),
        (new FieldDescriptor
        {
            Id = "ProviderId", Label = "Provider id", Group = "Identity", Type = FieldValueType.Text, Kinds = Msev, Operators = [SmartSearchQueryOperator.Eq],
            Options = [new FieldOptionSpec(FieldOption.Provider, true, Providers)], NeedsProviderIds = true
        }, new TextEvaluator(rule => row => ProviderIdOf(row.Item, rule.Provider), TextComparison.OrdinalIgnoreCase)),
        (new FieldDescriptor
        {
            Id = "Locked", Label = "Locked", Group = "Identity", Type = FieldValueType.Bool, Kinds = All, Operators = QueryOperators.Bool
        }, BoolEvaluator.Of(row => row.Item.IsLocked)),
    ];

    // The provider ids are keyed by MetadataProvider names ("Tmdb", "Imdb", "Tvdb"); compared ignoring case, whatever
    // comparer the item's dictionary has.
    private static string? ProviderIdOf(BaseItem item, string? provider)
    {
        if (provider is null || item.ProviderIds is not { Count: > 0 } ids)
        {
            return null;
        }

        var key = provider.Trim();
        foreach (var (name, id) in ids)
        {
            if (string.Equals(name, key, StringComparison.OrdinalIgnoreCase))
            {
                return id;
            }
        }

        return null;
    }
}
