using System;
using System.Collections.Generic;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.SmartSearch.Query;
using Jellyfin.Plugin.SmartSearch.Query.Evaluation;
using Jellyfin.Plugin.SmartSearch.Query.Fields;
using Jellyfin.Plugin.SmartSearch.Query.Validation;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.SmartSearch.Tests.Execution;

/// <summary>
/// A facts field for the tests (the plugin has none yet; the stream and rating fields will be the real ones): <c>TestFact</c>, a
/// number from 0 to 100 that only a facts source knows, derived from the item's id (empty for one id in seven), so a
/// test's truth can compute it for every row.
/// </summary>
internal static class TestFacts
{
    public const string FieldId = "TestFact";

    public static readonly FieldDescriptor Field = new()
    {
        Id = FieldId,
        Label = "Test fact",
        Group = "Test",
        Type = FieldValueType.Number,
        Kinds = [BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Episode, BaseItemKind.MusicVideo, BaseItemKind.Video, BaseItemKind.BoxSet],
        Operators = QueryOperators.Number,
        Min = 0,
        Max = 100,
        Cost = FieldCost.Facts
    };

    public static readonly FieldRegistry Registry = FieldRegistry.WithFields([(Field, new NumberEvaluator(row => row.Facts is { } facts && facts.TryGetValue(FieldId, out var value) ? value as double? : null))]);

    public static readonly QueryValidator Validator = new(Registry);

    /// <summary>The item's fact: the first two bytes of its id modulo 101, empty when they are a multiple of 7.</summary>
    public static double? Of(Guid id)
    {
        var bytes = id.ToByteArray();
        var n = (bytes[0] << 8) | bytes[1];
        return n % 7 == 0 ? null : n % 101;
    }

    /// <summary>The facts a source returns for an item.</summary>
    public static IReadOnlyDictionary<string, object?> For(BaseItem item) => new Dictionary<string, object?>(StringComparer.Ordinal) { [FieldId] = Of(item.Id) };
}
