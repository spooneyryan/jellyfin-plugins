using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.Library;
using MediaBrowser.Controller;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartSearch.Query.Planning;

/// <summary>
/// Runs the clean-form probe (<see cref="DatabasePushdownFacts.Refresh"/>) once after Jellyfin's startup, and again on each
/// library scan: in the scan's full pass (the plugin's post-scan task, <c>SmartSearchPostScanTask</c>) and in the batch of
/// changes core's own post-scan tasks raise after it (<see cref="LibraryChangeBatch.AfterFullPass"/>). Other batches
/// change nothing it keeps: a save rewrites the item's clean name, and new values get today's clean form.
/// </summary>
internal sealed class CleanFormProbe : KitBackgroundService, ILibraryChangeConsumer
{
    /// <summary>The consumer's name.</summary>
    public const string ConsumerName = "CleanFormProbe";

    private readonly DatabasePushdownFacts _facts;

    /// <summary>
    /// Initializes a new instance of the <see cref="CleanFormProbe"/> class.
    /// </summary>
    /// <param name="applicationHost">The server application host (the startup gate).</param>
    /// <param name="facts">The facts whose snapshot it refreshes.</param>
    /// <param name="logger">The logger.</param>
    public CleanFormProbe(IServerApplicationHost applicationHost, DatabasePushdownFacts facts, ILogger logger)
        : base(applicationHost, logger)
    {
        ArgumentNullException.ThrowIfNull(facts);
        _facts = facts;
    }

    /// <inheritdoc />
    public string Name => ConsumerName;

    /// <inheritdoc />
    public LibraryChangeFilter Filter { get; } = new()
    {
        Kinds = new HashSet<BaseItemKind>(QueryLimits.ItemKindOrder),
        CaptureIdentity = false
    };

    /// <inheritdoc />
    public Task HandleAsync(LibraryChangeBatch batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.FullPass || batch.AfterFullPass)
        {
            _facts.Refresh(cancellationToken);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override Task RunAsync(CancellationToken stoppingToken)
    {
        _facts.Refresh(stoppingToken);
        return Task.CompletedTask;
    }
}
