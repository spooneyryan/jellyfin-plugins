using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Jellyfin.Plugin.AccessRoles.Rules;

namespace Jellyfin.Plugin.AccessRoles.Tests.Rules;

/// <summary>
/// Writes an action manifest in its canonical form (one action per line, so a changed action is one changed line in a
/// diff) and builds the candidate a maintainer reviews after a Jellyfin update: every action of the DLL, with the rules and
/// reviews the current manifest gives the ones it already lists, and <see cref="UnreviewedMarker"/> on the new ones and on
/// the known ones whose routes, policies or parameters changed, which the manifest test refuses until someone reviews them
/// (<c>plugins/AccessRoles/DEVELOPMENT.md</c>, "The action manifest").
/// </summary>
internal static class ActionManifestWriter
{
    /// <summary>The review a candidate gives an action the manifest doesn't list yet, and the start of a changed one's.</summary>
    public const string UnreviewedMarker = "UNREVIEWED";

    private static readonly JsonWriterOptions _compact = new() { Indented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly JsonSerializerOptions _quote = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// Builds a candidate manifest's entries: a known action with the same shape keeps its rules and review; one whose shape
    /// changed keeps its rules and gets <c>"UNREVIEWED (changed: …) "</c> before its review, so that a policy dropped or a
    /// parameter renamed (which a rule reads) is looked at before the candidate can pass; a new one has no rules
    /// and the review <see cref="UnreviewedMarker"/>.
    /// </summary>
    /// <param name="current">The current manifest.</param>
    /// <param name="actual">The actions of the DLL.</param>
    /// <returns>The entries, by controller and action name.</returns>
    public static IReadOnlyList<ActionManifestEntry> Candidate(ActionManifest current, IEnumerable<ApiActionShape> actual)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(actual);
        return actual
            .Select(shape => current.Find(shape.Controller, shape.Action) is { } known
                ? Entry(shape, known.Rules, Review(known, shape))
                : Entry(shape, [], UnreviewedMarker))
            .OrderBy(e => e.Controller, StringComparer.Ordinal)
            .ThenBy(e => e.Action, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Gets a known action's review in a candidate: as it was when the action's shape is the reviewed one, else marked.
    /// </summary>
    /// <param name="known">The action as the current manifest has it.</param>
    /// <param name="shape">The action as the DLL has it.</param>
    /// <returns>The review.</returns>
    public static string Review(ActionManifestEntry known, ApiActionShape shape)
    {
        ArgumentNullException.ThrowIfNull(known);
        ArgumentNullException.ThrowIfNull(shape);
        var changes = known.ToShape().DescribeDifferences(shape);
        return changes.Count == 0 ? known.Review : $"{UnreviewedMarker} (changed: {string.Join("; ", changes)}) {known.Review}";
    }

    /// <summary>
    /// Writes a manifest.
    /// </summary>
    /// <param name="header">The manifest whose header fields are written.</param>
    /// <param name="entries">The entries.</param>
    /// <returns>The file's text (LF line ends, a final line end).</returns>
    public static string Write(ActionManifest header, IEnumerable<ActionManifestEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(entries);
        var text = new StringBuilder();
        text.Append("{\n");
        text.Append("  \"Format\": ").Append(Quote(header.Format)).Append(",\n");
        text.Append("  \"JellyfinTarget\": ").Append(Quote(header.JellyfinTarget)).Append(",\n");
        text.Append("  \"JellyfinVersion\": ").Append(Quote(header.JellyfinVersion)).Append(",\n");
        text.Append("  \"Source\": ").Append(Quote(header.Source)).Append(",\n");
        text.Append("  \"Assembly\": ").Append(Quote(header.Assembly)).Append(",\n");
        text.Append("  \"Actions\": [\n");
        text.Append(string.Join(",\n", entries.Select(e => "    " + Line(e))));
        text.Append("\n  ]\n}\n");
        return text.ToString();
    }

    private static ActionManifestEntry Entry(ApiActionShape shape, IReadOnlyList<ActionManifestRule> rules, string review) => new()
    {
        Controller = shape.Controller,
        Action = shape.Action,
        Routes = shape.Routes,
        Policies = shape.Policies,
        AllowAnonymous = shape.AllowAnonymous,
        Parameters = shape.Parameters,
        Rules = rules,
        Review = review
    };

    private static string Line(ActionManifestEntry entry)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, _compact))
        {
            writer.WriteStartObject();
            writer.WriteString("Controller", entry.Controller);
            writer.WriteString("Action", entry.Action);
            WriteStrings(writer, "Routes", entry.Routes);
            WriteStrings(writer, "Policies", entry.Policies);
            if (entry.AllowAnonymous)
            {
                writer.WriteBoolean("AllowAnonymous", true);
            }

            WriteStrings(writer, "Parameters", entry.Parameters);
            writer.WriteStartArray("Rules");
            foreach (var rule in entry.Rules)
            {
                writer.WriteStartObject();
                writer.WriteString("Kind", rule.Kind.ToString());
                if (rule.Lock is { } locked)
                {
                    writer.WriteString("Lock", locked.ToString());
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteString("Review", entry.Review);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteStrings(Utf8JsonWriter writer, string name, IEnumerable<string> values)
    {
        writer.WriteStartArray(name);
        foreach (var value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }

    private static string Quote(string value) => JsonSerializer.Serialize(value, _quote);
}
