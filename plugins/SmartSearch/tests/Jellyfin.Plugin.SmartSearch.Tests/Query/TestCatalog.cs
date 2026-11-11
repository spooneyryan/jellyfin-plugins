using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.SmartSearch.Api.Models;
using Jellyfin.Plugin.SmartSearch.Query;
using Jellyfin.Plugin.SmartSearch.Query.Fields;
using Jellyfin.Plugin.SmartSearch.Query.Validation;
using Xunit;

namespace Jellyfin.Plugin.SmartSearch.Tests.Query;

/// <summary>
/// A field catalog with one field of each shape the validator and the canonicalizer handle, shaped like the real
/// registry's (and independent of it): text with an option and an alias, an admin-only text, an integer with a span,
/// a rating, a date, a list, choices of ids, kinds, references and fixed strings, a rating string with <c>Le</c>/<c>Ge</c>,
/// per-user and option-required bools, and an unavailable field.
/// </summary>
internal static class TestCatalog
{
    private static readonly BaseItemKind[] All = [BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Episode, BaseItemKind.MusicVideo, BaseItemKind.Video, BaseItemKind.BoxSet];
    private static readonly BaseItemKind[] Videos = [BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.MusicVideo, BaseItemKind.Video];

    public static FieldCatalog Instance { get; } = new(
        [
            new FieldDescriptor
            {
                Id = "Title", Label = "Title", Group = "Text", Type = FieldValueType.Text, Kinds = All, Operators = QueryOperators.Text,
                Options = [new FieldOptionSpec(FieldOption.IncludeOriginalTitle, false, null)], Aliases = ["Name"]
            },
            new FieldDescriptor
            {
                Id = "Overview", Label = "Overview", Group = "Text", Type = FieldValueType.Text, Kinds = All,
                Operators = [SmartSearchQueryOperator.Contains, SmartSearchQueryOperator.NotContains, SmartSearchQueryOperator.IsEmpty, SmartSearchQueryOperator.IsNotEmpty]
            },
            new FieldDescriptor
            {
                Id = "Path", Label = "Path", Group = "Text", Type = FieldValueType.Text, Kinds = All, AdminOnly = true,
                Operators = [SmartSearchQueryOperator.Contains, SmartSearchQueryOperator.NotContains, SmartSearchQueryOperator.StartsWith, SmartSearchQueryOperator.Matches]
            },
            new FieldDescriptor
            {
                Id = "Year", Label = "Year", Group = "Dates", Type = FieldValueType.Number, Kinds = [BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Episode, BaseItemKind.MusicVideo, BaseItemKind.Video],
                Operators = [.. QueryOperators.Number, SmartSearchQueryOperator.AnyOf], ElementKind = FieldElementKind.Integer,
                Min = QueryLimits.MinYear, Max = QueryLimits.MaxYear, Integer = true, MaxSpan = QueryLimits.MaxYearSpan
            },
            new FieldDescriptor { Id = "PremiereDate", Label = "Premiere date", Group = "Dates", Type = FieldValueType.Date, Kinds = All, Operators = QueryOperators.Date },
            new FieldDescriptor { Id = "CommunityRating", Label = "Community rating", Group = "Ratings", Type = FieldValueType.Number, Kinds = All, Operators = QueryOperators.Number, Min = 0, Max = 10 },
            new FieldDescriptor { Id = "Genres", Label = "Genres", Group = "Metadata", Type = FieldValueType.List, Kinds = All, Operators = QueryOperators.List, ElementKind = FieldElementKind.String, ValueSource = "/SmartSearch/Values/Genres" },
            new FieldDescriptor { Id = "Library", Label = "Library", Group = "Scope", Type = FieldValueType.Choice, Kinds = All, Operators = QueryOperators.Choice, ElementKind = FieldElementKind.Guid, Cost = FieldCost.Set },
            new FieldDescriptor { Id = "ItemType", Label = "Item type", Group = "Scope", Type = FieldValueType.Choice, Kinds = All, Operators = QueryOperators.Choice, ElementKind = FieldElementKind.ItemKind },
            new FieldDescriptor { Id = "Series", Label = "Series", Group = "Scope", Type = FieldValueType.Choice, Kinds = [BaseItemKind.Episode], Operators = QueryOperators.Choice, ElementKind = FieldElementKind.Reference },
            new FieldDescriptor
            {
                Id = "SeriesStatus", Label = "Series status", Group = "Metadata", Type = FieldValueType.Choice, Kinds = [BaseItemKind.Series], Operators = QueryOperators.Choice,
                ElementKind = FieldElementKind.String, Choices = ["Continuing", "Ended", "Unreleased"]
            },
            new FieldDescriptor
            {
                Id = "OfficialRating", Label = "Parental rating", Group = "Metadata", Type = FieldValueType.Choice, Kinds = All, ElementKind = FieldElementKind.String,
                Operators = [SmartSearchQueryOperator.AnyOf, SmartSearchQueryOperator.NoneOf, SmartSearchQueryOperator.Le, SmartSearchQueryOperator.Ge]
            },
            new FieldDescriptor { Id = "Played", Label = "Played", Group = "Watch state", Type = FieldValueType.Bool, Kinds = Videos, Operators = QueryOperators.Bool, PerUser = true, Cost = FieldCost.User },
            new FieldDescriptor
            {
                Id = "HasProviderId", Label = "Has provider id", Group = "Identity", Type = FieldValueType.Bool, Kinds = [BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Episode, BaseItemKind.MusicVideo],
                Operators = QueryOperators.Bool, Options = [new FieldOptionSpec(FieldOption.Provider, true, ["Tmdb", "Imdb", "Tvdb"])]
            },
            new FieldDescriptor { Id = "MemberRating", Label = "Member rating", Group = "Ratings", Type = FieldValueType.Number, Kinds = All, Operators = QueryOperators.Number, Min = 0.5, Max = 5, Available = false, Cost = FieldCost.Facts },
        ],
        ["SortName", "ProductionYear", "PremiereDate", "DateCreated", "CommunityRating", "Random"]);

    public static QueryValidator Validator { get; } = new(Instance);

    public static QueryCanonicalizer Canonicalizer { get; } = new(Instance);

    /// <summary>A query over the given kinds whose root group has the given children (JSON text).</summary>
    public static string WithChildren(string children, string kinds = "\"Movie\"") => "{\"Scope\":{\"ItemKinds\":[" + kinds + "]},\"Where\":{\"Children\":[" + children + "]}}";

    /// <summary>Reads a query that must read.</summary>
    public static SmartSearchQueryDefinition Read(string json)
    {
        var read = QueryJson.Read(json);
        Assert.True(read.Definition is not null, "The query didn't read: " + string.Join(", ", read.Errors.Select(e => e.Path + ":" + e.Code)));
        return read.Definition!;
    }

    /// <summary>Gets an administrator's context with <c>EnableRegex</c> on, for the dormant <c>Matches</c> code.</summary>
    public static QueryValidationContext AdminWithRegex { get; } = QueryValidationContext.Admin with { EnableRegex = true };

    /// <summary>Reads and validates a query; the errors as <c>path:code</c>.</summary>
    public static IReadOnlyList<string> Errors(string json, QueryValidationContext? context = null)
        => Validate(json, context).Errors.Select(e => e.Path + ":" + e.Code).ToList();

    /// <summary>Reads and validates a query.</summary>
    public static QueryValidationResult Validate(string json, QueryValidationContext? context = null)
        => Validator.Validate(Read(json), context ?? QueryValidationContext.User);

    /// <summary>The hash of a query that must read.</summary>
    public static string Hash(string json) => Canonicalizer.Canonicalize(Read(json)).Hash;

    /// <summary>The canonical JSON of a query that must read.</summary>
    public static string Canonical(string json) => Canonicalizer.Canonicalize(Read(json)).Json;
}
