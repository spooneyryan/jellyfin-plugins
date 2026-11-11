using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Kit.Diagnostics;

/// <summary>
/// A validated <c>POST /&lt;Route&gt;/Diagnostics/ClientHealth</c> body. Any signed-in
/// user can post one, so nothing in it is trusted: <see cref="TryParse"/> accepts a report only when every field
/// matches its rule, and the plugin's anchor list names every anchor and view.
/// </summary>
/// <param name="WebVersion">The jellyfin-web version, or <c>null</c> inside a native app wrapper (<c>window.NativeShell</c>),
/// where <c>ApiClient.appVersion()</c> is the app's version.</param>
/// <param name="Client"><c>ApiClient.appName()</c>, for example <c>Jellyfin Web</c>.</param>
/// <param name="ClientVersion"><c>ApiClient.appVersion()</c>.</param>
/// <param name="Layout">The web client's layout.</param>
/// <param name="Experimental">Whether the experimental layout is on.</param>
/// <param name="PluginVersion">The plugin version of the script that reported, or <c>null</c>.</param>
/// <param name="Missing">The anchors found missing, each with its view and how often, at least one.</param>
internal sealed partial record ClientHealthReport(
    string? WebVersion,
    string Client,
    string ClientVersion,
    ClientLayout Layout,
    bool Experimental,
    string? PluginVersion,
    IReadOnlyList<ClientHealthMissing> Missing)
{
    /// <summary>The largest body the endpoint reads (413 over 16 KB).</summary>
    public const int MaxBodyBytes = 16 * 1024;

    /// <summary>The most entries in <see cref="Missing"/>.</summary>
    public const int MaxMissing = 32;

    /// <summary>The largest <see cref="ClientHealthMissing.Count"/>.</summary>
    public const int MaxCount = 1000;

    private static readonly string[] _properties = ["WebVersion", "Client", "ClientVersion", "Layout", "Experimental", "PluginVersion", "Missing"];

    /// <summary>
    /// Gets the key the server de-duplicates on: <see cref="WebVersion"/>, or <c>native:&lt;Client&gt;</c> without one.
    /// </summary>
    public string WebKey => WebVersion ?? "native:" + Client;

    /// <summary>
    /// Gets the web client as the log and Diagnostics word it: <c>web 10.11.5</c>, or
    /// <c>web unknown (Jellyfin Media Player 1.2.3)</c>.
    /// </summary>
    public string WebText => WebVersion is null ? $"web unknown ({Client} {ClientVersion})" : "web " + WebVersion;

    /// <summary>
    /// Parses and validates a report. Property names match case-insensitively, as Jellyfin's own JSON input does; other
    /// properties are ignored (dropped).
    /// </summary>
    /// <param name="json">The request body.</param>
    /// <param name="catalog">The plugin's anchors.</param>
    /// <param name="report">The report, when valid.</param>
    /// <param name="reason">Why it isn't, for a Debug log line; never shown to the caller.</param>
    /// <returns><c>true</c> when the report is valid.</returns>
    public static bool TryParse(ReadOnlyMemory<byte> json, WebAnchorCatalog catalog, out ClientHealthReport? report, out string reason)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        report = null;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 6 });
            return TryRead(document.RootElement, catalog, out report, out reason);
        }
        catch (JsonException)
        {
            reason = "the body isn't JSON (" + json.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + " bytes)";
            return false;
        }
    }

    /// <summary>
    /// Gets a value indicating whether a string is a version as the report's version fields need it:
    /// <c>^[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3}(\.[0-9]{1,5})?$</c>: the web client's <c>\d</c> pattern as JavaScript
    /// reads it, ASCII digits only.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns><c>true</c> when it matches.</returns>
    public static bool IsVersion(string? value) => value is not null && Version().IsMatch(value);

    /// <summary>
    /// Gets a value indicating whether a string is a client name: <c>^[A-Za-z0-9 ._()+-]{1,64}$</c>.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns><c>true</c> when it matches.</returns>
    public static bool IsClientName(string? value) => value is not null && ClientName().IsMatch(value);

    private static bool TryRead(JsonElement root, WebAnchorCatalog catalog, out ClientHealthReport? report, out string reason)
    {
        report = null;
        if (root.ValueKind != JsonValueKind.Object)
        {
            reason = "the body isn't a JSON object";
            return false;
        }

        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            var known = _properties.FirstOrDefault(p => p.Equals(property.Name, StringComparison.OrdinalIgnoreCase));
            if (known is null)
            {
                continue;
            }

            if (!fields.TryAdd(known, property.Value))
            {
                reason = $"{known} appears twice";
                return false;
            }
        }

        string? webVersion = null;
        if (fields.TryGetValue("WebVersion", out var web) && web.ValueKind != JsonValueKind.Null)
        {
            webVersion = web.ValueKind == JsonValueKind.String ? web.GetString() : null;
            if (!IsVersion(webVersion))
            {
                reason = "WebVersion is neither null nor a version";
                return false;
            }
        }

        var client = String(fields, "Client");
        if (!IsClientName(client))
        {
            reason = "Client doesn't match its pattern";
            return false;
        }

        var clientVersion = String(fields, "ClientVersion");
        if (!IsVersion(clientVersion))
        {
            reason = "ClientVersion isn't a version";
            return false;
        }

        var layoutName = String(fields, "Layout");
        if (layoutName is null || !LayoutName().IsMatch(layoutName))
        {
            reason = "Layout isn't Desktop, Mobile or Tv";
            return false;
        }

        var layout = Enum.Parse<ClientLayout>(layoutName);

        var experimental = false;
        if (fields.TryGetValue("Experimental", out var exp) && exp.ValueKind != JsonValueKind.Null)
        {
            if (exp.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                reason = "Experimental isn't a boolean";
                return false;
            }

            experimental = exp.GetBoolean();
        }

        string? pluginVersion = null;
        if (fields.TryGetValue("PluginVersion", out var plugin) && plugin.ValueKind != JsonValueKind.Null)
        {
            pluginVersion = plugin.ValueKind == JsonValueKind.String ? plugin.GetString() : null;
            if (pluginVersion is null || !PluginVersionPattern().IsMatch(pluginVersion))
            {
                reason = "PluginVersion isn't a version";
                return false;
            }
        }

        if (!TryReadMissing(fields, catalog, out var missing, out reason))
        {
            return false;
        }

        report = new ClientHealthReport(webVersion, client!, clientVersion!, layout, experimental, pluginVersion, missing);
        reason = string.Empty;
        return true;
    }

    private static bool TryReadMissing(Dictionary<string, JsonElement> fields, WebAnchorCatalog catalog, out List<ClientHealthMissing> missing, out string reason)
    {
        missing = [];
        if (!fields.TryGetValue("Missing", out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() is 0 or > MaxMissing)
        {
            reason = $"Missing isn't an array of 1 to {MaxMissing} entries";
            return false;
        }

        foreach (var entry in list.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                reason = "a Missing entry isn't an object";
                return false;
            }

            string? anchor = null;
            string? view = null;
            var count = 1;
            foreach (var property in entry.EnumerateObject())
            {
                if (property.Name.Equals("Anchor", StringComparison.OrdinalIgnoreCase))
                {
                    anchor = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
                }
                else if (property.Name.Equals("View", StringComparison.OrdinalIgnoreCase))
                {
                    view = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
                }
                else if (property.Name.Equals("Count", StringComparison.OrdinalIgnoreCase))
                {
                    if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out count) || count is < 1 or > MaxCount)
                    {
                        reason = $"a Missing Count isn't a whole number from 1 to {MaxCount}";
                        return false;
                    }
                }
            }

            if (anchor is null || view is null || !catalog.Accepts(anchor, view))
            {
                reason = "a Missing entry names an anchor or view the plugin doesn't declare together";
                return false;
            }

            if (missing.Any(m => m.Anchor == anchor && m.View == view))
            {
                reason = "a Missing entry appears twice";
                return false;
            }

            missing.Add(new ClientHealthMissing(anchor, view, count));
        }

        reason = string.Empty;
        return true;
    }

    private static string? String(Dictionary<string, JsonElement> fields, string name)
        => fields.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // \z, not $: $ also matches before a final newline, which would let "10.11.5\n" or a client name with a line break
    // into the log. [0-9], not \d: .NET's \d is any Unicode decimal digit, so versions in Arabic-Indic or other digits would
    // pass into the log and Diagnostics; the patterns come from the web client's JavaScript, where \d is 0-9 only.
    [GeneratedRegex(@"^[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3}(\.[0-9]{1,5})?\z")]
    private static partial Regex Version();

    [GeneratedRegex(@"^[A-Za-z0-9 ._()+-]{1,64}\z")]
    private static partial Regex ClientName();

    [GeneratedRegex(@"^[0-9]{1,5}(\.[0-9]{1,5}){1,3}\z")]
    private static partial Regex PluginVersionPattern();

    [GeneratedRegex(@"^(Desktop|Mobile|Tv)\z")]
    private static partial Regex LayoutName();
}
