using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.Kit.Web;
using Jellyfin.Plugin.Kit.Web.Pages;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Kit.Diagnostics;

/// <summary>
/// The kit's diagnostics (<c>ClientHealthRegistry</c>, <c>KitDiagnostics</c>): the ClientHealth
/// endpoint and the kit's part of the admin Diagnostics body.
/// </summary>
internal sealed class KitDiagnostics : IKitDiagnostics
{
    /// <summary>The 413 message.</summary>
    public const string TooLargeMessage = "A client health report is at most 16 KB.";

    /// <summary>The most kept copies of a store file <see cref="DescribeStore{TDoc}"/> lists, the newest.</summary>
    public const int MaxKeptFiles = 20;

    private readonly KitOptions _options;
    private readonly KitAbiStatus _abi;
    private readonly ClientHealthRegistry _registry;
    private readonly IServiceProvider _services;
    private readonly IReadOnlyList<IKitDiagnosticsSource> _sources;

    /// <summary>
    /// Initializes a new instance of the <see cref="KitDiagnostics"/> class.
    /// </summary>
    /// <param name="options">The plugin's kit options.</param>
    /// <param name="abi">The ABI guard's outcome.</param>
    /// <param name="registry">The health reports.</param>
    /// <param name="services">The server's services, for the settings and the optional page hosts.</param>
    /// <param name="sources">The plugin's own entries, such as its store.</param>
    public KitDiagnostics(KitOptions options, KitAbiStatus abi, ClientHealthRegistry registry, IServiceProvider services, IEnumerable<IKitDiagnosticsSource> sources)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(abi);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(sources);

        _options = options;
        _abi = abi;
        _registry = registry;
        _services = services;
        _sources = sources.ToList();
    }

    /// <inheritdoc />
    public async Task<IActionResult> ReceiveClientHealthAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var userId = context.User.GetUserId();
        if (context.User.IsApiKey() || userId.Equals(Guid.Empty))
        {
            throw PluginException.Forbidden(CurrentUser.NoUserMessage);
        }

        if (context.Request.ContentLength > ClientHealthReport.MaxBodyBytes)
        {
            _registry.Count(ClientHealthOutcome.TooLarge);
            return KitProblemDetails.Result(PluginErrorKind.PayloadTooLarge, TooLargeMessage);
        }

        if (!ReportsEnabled(context.RequestServices))
        {
            _registry.Count(ClientHealthOutcome.Disabled);
            return new NoContentResult();
        }

        var body = await ReadBodyAsync(context.Request).ConfigureAwait(false);
        if (body is null)
        {
            _registry.Count(ClientHealthOutcome.TooLarge);
            return KitProblemDetails.Result(PluginErrorKind.PayloadTooLarge, TooLargeMessage);
        }

        _registry.Submit(userId, body.Value);
        return new NoContentResult();
    }

    /// <inheritdoc />
    public IDictionary<string, object?> Describe()
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["PluginVersion"] = IndexHtmlMarkers.Version(_options.PluginAssembly),
            ["KitVersion"] = KitVersion(),
            ["Abi"] = _abi.ToSummary()
        };

        foreach (var source in _sources)
        {
            try
            {
                result[source.Key] = source.Describe();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or PluginException)
            {
                result[source.Key] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["Error"] = ex.Message };
            }
        }

        if (_services.GetService<KitPageHosts>() is { } hosts)
        {
            result["Pages"] = hosts.Entries.Select(e => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["Key"] = e.Key,
                ["Host"] = e.Host.ToString(),
                ["Detail"] = e.Detail
            }).ToList();
        }

        var counts = _registry.Counts;
        result["WebAnchors"] = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Reporting"] = ReportsEnabled(_services),
            ["Declared"] = _registry.Catalog.Anchors.Select(a => a.Name).ToList(),
            ["Problems"] = _registry.Catalog.Problems,
            ["Reports"] = Enum.GetValues<ClientHealthOutcome>().ToDictionary(o => o.ToString(), o => counts[o], StringComparer.Ordinal),
            // ClientHealth holds at most this many entries; Reports.Overflow counts the older ones that made room.
            ["MaxEntries"] = ClientHealthRegistry.MaxEntries
        };
        result["ClientHealth"] = _registry.Entries.Select(Describe).ToList();
        return result;
    }

    /// <summary>
    /// Describes a JSON store for Diagnostics (<c>Store:{Version, Status, File, LastSnapshotUtc}</c>), with the
    /// copies of its file kept next to it (<c>.v&lt;N&gt;-*</c> from before an upgrade and <c>.corrupt-*</c>;
    /// <c>KeptFiles</c>, the newest <see cref="MaxKeptFiles"/>, and <c>KeptFileCount</c>). The store is loaded first if
    /// nothing has loaded it yet, so the copy that load's upgrade keeps is listed along with the message naming it.
    /// </summary>
    /// <typeparam name="TDoc">The store's document.</typeparam>
    /// <param name="store">The store.</param>
    /// <returns>The description.</returns>
    public static IDictionary<string, object?> DescribeStore<TDoc>(JsonDocumentStore<TDoc> store)
        where TDoc : class, IVersionedDocument
    {
        ArgumentNullException.ThrowIfNull(store);

        // Load before listing: the first load may upgrade the file and keep a .v<N>-* copy.
        var status = store.Status;
        var kept = store.ListKeptFiles();
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Name"] = store.StoreName,
            ["File"] = store.FilePath,
            ["Version"] = store.LoadedVersion ?? store.CurrentVersion,
            ["CurrentVersion"] = store.CurrentVersion,
            ["Status"] = status.ToString(),
            ["ReadOnly"] = store.IsReadOnly,
            ["Message"] = store.StatusMessage,
            ["LastSnapshotUtc"] = store.LastSnapshotUtc,
            ["KeepSnapshotsBeforeUtc"] = store.KeepSnapshotsBeforeUtc,
            ["KeptFiles"] = kept.Take(MaxKeptFiles).Select(f => f.ToDictionary()).ToList(),
            ["KeptFileCount"] = kept.Count
        };
    }

    private static IDictionary<string, object?> Describe(ClientHealthEntry entry) => new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["Anchor"] = entry.Anchor,
        ["View"] = entry.View,
        ["Required"] = entry.Required,
        ["WebVersion"] = entry.WebVersion,
        ["Client"] = entry.Client,
        ["ClientVersion"] = entry.ClientVersion,
        ["Layout"] = entry.Layout.ToString(),
        ["Experimental"] = entry.Experimental,
        ["PluginVersion"] = entry.PluginVersion,
        ["Count"] = entry.Count,
        ["Reports"] = entry.Reports,
        ["Users"] = entry.Users,
        ["FirstUtc"] = entry.FirstUtc.UtcDateTime,
        ["LastUtc"] = entry.LastUtc.UtcDateTime
    };

    // Reads at most MaxBodyBytes; null when the body is larger (Kestrel's own limit, set by [RequestSizeLimit], may also
    // stop the read with a 413 BadHttpRequestException).
    private static async Task<ReadOnlyMemory<byte>?> ReadBodyAsync(HttpRequest request)
    {
        var buffer = new byte[ClientHealthReport.MaxBodyBytes + 1];
        var length = 0;
        try
        {
            while (length < buffer.Length)
            {
                var read = await request.Body.ReadAsync(buffer.AsMemory(length), request.HttpContext.RequestAborted).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                length += read;
            }
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return null;
        }

        // Not a conditional expression: null would convert to Memory<byte> (through byte[]) as an empty, non-null body.
        if (length > ClientHealthReport.MaxBodyBytes)
        {
            return null;
        }

        return new ReadOnlyMemory<byte>(buffer, 0, length);
    }

    private static string KitVersion()
    {
        try
        {
            return KitBuild.KitVersion;
        }
        catch (InvalidOperationException)
        {
            return "unknown";
        }
    }

    private bool ReportsEnabled(IServiceProvider services)
    {
        try
        {
            return _options.ReportWebAnchorProblems(services);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }
}
