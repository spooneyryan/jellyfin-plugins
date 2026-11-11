using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.SmartSearch.Api.Models;
using Jellyfin.Plugin.SmartSearch.Query.Fields;
using Jellyfin.Plugin.SmartSearch.Query.Validation;

namespace Jellyfin.Plugin.SmartSearch.Query;

/// <summary>
/// The canonical JSON of a query and its hash, <c>QueryHash</c>: equivalent queries give the
/// same text, any change of meaning a different one. The result cache keys on the hash, and the auto-collections compare it to
/// see a query change.
/// </summary>
/// <remarks>
/// <para>The form, compact and in a fixed property order:</para>
/// <list type="bullet">
/// <item><c>Version</c> always; <c>Sort</c> unless it is none or by title ascending; <c>Limit</c> when set; <c>Extra</c> never.</item>
/// <item>Scope: item kinds by name, once each; library ids as 32 hex digits, sorted, once each, left out when none; <c>IncludeMissing</c> only when true.</item>
/// <item>Groups: <c>Match</c> only when <c>Any</c>, <c>Negate</c> only when true, the children sorted by their own canonical text (AND and OR don't depend on order).</item>
/// <item>Rules: the field id (never an alias); the operator; only the values it reads; text trimmed (not a <c>Matches</c> pattern), numbers in their shortest form, dates as <c>yyyy-MM-dd</c> when they are days or else the UTC time with <c>Z</c>, a field's fixed choices in their own spelling; <c>Values</c> once each and sorted (references by id); options only when set, and <c>IncludeOriginalTitle</c> only when true.</item>
/// </list>
/// <para>It is computed from a valid query (<see cref="QueryValidator"/>); for anything else it is still deterministic.</para>
/// </remarks>
internal sealed class QueryCanonicalizer
{
    private const string HashPrefix = "sha256:";

    private static readonly JsonWriterOptions _writerOptions = new() { Indented = false };

    private readonly IFieldCatalog _catalog;

    /// <summary>
    /// Initializes a new instance of the <see cref="QueryCanonicalizer"/> class.
    /// </summary>
    /// <param name="catalog">The fields and sort keys, for ids, aliases and spellings.</param>
    public QueryCanonicalizer(IFieldCatalog catalog)
    {
        _catalog = catalog;
    }

    /// <summary>Gets the canonical JSON and hash of a query.</summary>
    /// <param name="query">The query, valid.</param>
    /// <returns>The canonical form.</returns>
    public CanonicalQuery Canonicalize(SmartSearchQueryDefinition query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var json = Write(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("Version", query.Version);
            w.WritePropertyName("Scope");
            WriteScope(w, query.Scope);
            w.WritePropertyName("Where");
            w.WriteRawValue(Node(query.Where ?? SmartSearchQueryNode.EmptyAll()));
            var sort = (query.Sort ?? []).Where(s => s is not null).ToList();
            if (!IsDefaultSort(sort))
            {
                w.WriteStartArray("Sort");
                foreach (var key in sort)
                {
                    w.WriteStartObject();
                    w.WriteString("Key", SortKey(key.Key));
                    if (key.Order != SortOrder.Ascending)
                    {
                        w.WriteString("Order", key.Order.ToString());
                    }

                    w.WriteEndObject();
                }

                w.WriteEndArray();
            }

            if (query.Limit is { } limit)
            {
                w.WriteNumber("Limit", limit);
            }

            w.WriteEndObject();
        });
        return new CanonicalQuery(json, HashPrefix + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json))));
    }

    private static string Write(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, _writerOptions))
        {
            write(writer);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteScope(Utf8JsonWriter w, SmartSearchQueryScope? scope)
    {
        w.WriteStartObject();
        w.WriteStartArray("ItemKinds");
        foreach (var kind in (scope?.ItemKinds ?? []).Select(k => k.ToString()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            w.WriteStringValue(kind);
        }

        w.WriteEndArray();
        var libraries = (scope?.LibraryIds ?? []).Select(id => id.ToString("N")).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (libraries.Count > 0)
        {
            w.WriteStartArray("LibraryIds");
            libraries.ForEach(w.WriteStringValue);
            w.WriteEndArray();
        }

        if (scope?.IncludeMissing == true)
        {
            w.WriteBoolean("IncludeMissing", true);
        }

        w.WriteEndObject();
    }

    private static bool IsDefaultSort(List<SmartSearchQuerySort> sort)
        => sort.Count == 0
           || (sort.Count == 1 && string.Equals(sort[0].Key?.Trim(), "SortName", StringComparison.OrdinalIgnoreCase) && sort[0].Order == SortOrder.Ascending);

    private string SortKey(string? key)
    {
        var trimmed = (key ?? string.Empty).Trim();
        return _catalog.FindSortKey(trimmed) ?? trimmed;
    }

    private string Node(SmartSearchQueryNode node)
    {
        if (node.IsGroup)
        {
            var children = node.Children!.Where(c => c is not null).Select(Node).Order(StringComparer.Ordinal).ToList();
            return Write(w =>
            {
                w.WriteStartObject();
                if (node.Match == SmartSearchMatchMode.Any)
                {
                    w.WriteString("Match", nameof(SmartSearchMatchMode.Any));
                }

                if (node.Negate)
                {
                    w.WriteBoolean("Negate", true);
                }

                w.WriteStartArray("Children");
                children.ForEach(c => w.WriteRawValue(c));
                w.WriteEndArray();
                w.WriteEndObject();
            });
        }

        var field = node.Field is null ? null : _catalog.Find(node.Field.Trim());
        return Write(w =>
        {
            w.WriteStartObject();
            w.WriteString("Field", field?.Id ?? node.Field?.Trim());
            if (node.Op is not { } op)
            {
                w.WriteEndObject();
                return;
            }

            w.WriteString("Op", op.ToString());
            switch (QueryOperators.ShapeOf(op))
            {
                case OperatorShape.Value when node.Value is { } value:
                    w.WritePropertyName("Value");
                    WriteScalar(w, field, op, value);
                    break;
                case OperatorShape.Range:
                    if (node.From is { } from)
                    {
                        w.WritePropertyName("From");
                        WriteScalar(w, field, op, from);
                    }

                    if (node.To is { } to)
                    {
                        w.WritePropertyName("To");
                        WriteScalar(w, field, op, to);
                    }

                    break;
                case OperatorShape.Relative:
                    if (node.Amount is { } amount)
                    {
                        w.WriteNumber("Amount", amount);
                    }

                    if (node.Unit is { } unit)
                    {
                        w.WriteString("Unit", unit.ToString());
                    }

                    break;
                case OperatorShape.Values when node.Values is { } values:
                    w.WriteStartArray("Values");
                    foreach (var element in CanonicalValues(field, values))
                    {
                        w.WriteRawValue(element);
                    }

                    w.WriteEndArray();
                    break;
            }

            if (node.IncludeOriginalTitle == true)
            {
                w.WriteBoolean("IncludeOriginalTitle", true);
            }

            if (!string.IsNullOrWhiteSpace(node.Role))
            {
                w.WriteString("Role", OptionChoice(field, FieldOption.Role, node.Role));
            }

            if (!string.IsNullOrWhiteSpace(node.Provider))
            {
                w.WriteString("Provider", OptionChoice(field, FieldOption.Provider, node.Provider));
            }

            w.WriteEndObject();
        });
    }

    private static string OptionChoice(FieldDescriptor? field, FieldOption option, string value)
    {
        var trimmed = value.Trim();
        var choices = field?.Options.FirstOrDefault(o => o.Option == option)?.Choices;
        return choices?.FirstOrDefault(c => string.Equals(c, trimmed, StringComparison.OrdinalIgnoreCase)) ?? trimmed;
    }

    private static string Choice(FieldDescriptor? field, string text)
        => field?.Choices?.FirstOrDefault(c => string.Equals(c, text, StringComparison.OrdinalIgnoreCase)) ?? text;

    private static void WriteScalar(Utf8JsonWriter w, FieldDescriptor? field, SmartSearchQueryOperator op, JsonElement value)
    {
        switch (field?.Type)
        {
            case FieldValueType.Number when value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number):
                WriteNumber(w, number);
                return;
            case FieldValueType.Date when value.ValueKind == JsonValueKind.String && QueryDates.TryParse(value.GetString()!, out var date):
                w.WriteStringValue(QueryDates.Format(date));
                return;
            case FieldValueType.Text or FieldValueType.Choice when value.ValueKind == JsonValueKind.String:
                var text = value.GetString()!;
                w.WriteStringValue(op == SmartSearchQueryOperator.Matches ? text : Choice(field, text.Trim()));
                return;
            default:
                w.WriteRawValue(value.GetRawText());
                return;
        }
    }

    private static void WriteNumber(Utf8JsonWriter w, double number)
    {
        if (number == Math.Floor(number) && Math.Abs(number) < 1e15)
        {
            w.WriteNumberValue((long)number); // also turns -0 into 0
        }
        else
        {
            w.WriteNumberValue(number);
        }
    }

    // Each entry as canonical JSON text, once each, in a fixed order: numbers by value, the rest ordinally, references by id.
    private static IEnumerable<string> CanonicalValues(FieldDescriptor? field, List<JsonElement> values)
    {
        switch (field?.ElementKind)
        {
            case FieldElementKind.Integer when values.All(v => v.ValueKind == JsonValueKind.Number):
                return values.Select(v => v.GetDouble()).Distinct().Order().Select(n => Write(w => WriteNumber(w, n)));
            case FieldElementKind.Guid when values.All(v => v.ValueKind == JsonValueKind.String && Guid.TryParse(v.GetString(), out _)):
                return Strings(values.Select(v => Guid.Parse(v.GetString()!).ToString("N")));
            case FieldElementKind.ItemKind when values.All(v => QueryValidator.ItemKindOf(v) is not null):
                return Strings(values.Select(v => QueryValidator.ItemKindOf(v)!.Value.ToString()));
            case FieldElementKind.String when values.All(v => v.ValueKind == JsonValueKind.String):
                return Strings(values.Select(v => Choice(field, v.GetString()!.Trim())));
            case FieldElementKind.Reference when values.All(v => QueryReference.Read(v).Valid):
                return values
                    .Select(QueryReference.Read)
                    .Select(v => (Id: v.Id.ToString("N"), v.Name))
                    .OrderBy(r => r.Id, StringComparer.Ordinal)
                    .ThenBy(r => r.Name, StringComparer.Ordinal)
                    .DistinctBy(r => r.Id)
                    .Select(r => Write(w =>
                    {
                        w.WriteStartObject();
                        w.WriteString("Id", r.Id);
                        if (r.Name is not null)
                        {
                            w.WriteString("Name", r.Name);
                        }

                        w.WriteEndObject();
                    }));
            default:
                return values.Select(v => Write(w => w.WriteRawValue(v.GetRawText()))).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        }
    }

    private static IEnumerable<string> Strings(IEnumerable<string> values)
        => values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(s => Write(w => w.WriteStringValue(s)));
}
