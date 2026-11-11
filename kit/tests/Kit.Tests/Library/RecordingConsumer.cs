using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Library;
using MediaBrowser.Controller.Library;
using Xunit;

namespace Jellyfin.Plugin.Kit.Tests.Library;

/// <summary>
/// A library-change consumer that records every batch with whether a scan was running when it arrived, and can hold or
/// fail a batch.
/// </summary>
internal sealed class RecordingConsumer : ILibraryChangeConsumer
{
    private readonly ILibraryManager? _library;
    private readonly ConcurrentQueue<Received> _received = new();

    public RecordingConsumer(string name, LibraryChangeFilter? filter = null, ILibraryManager? library = null)
    {
        Name = name;
        Filter = filter ?? new LibraryChangeFilter();
        _library = library;
    }

    public string Name { get; }

    public LibraryChangeFilter Filter { get; }

    /// <summary>Gets or sets what each call does after it recorded the batch (hold it, throw).</summary>
    public Func<LibraryChangeBatch, CancellationToken, Task>? OnHandle { get; set; }

    public IReadOnlyList<Received> Received => _received.ToList();

    public IReadOnlyList<LibraryChangeBatch> Batches => _received.Select(r => r.Batch).ToList();

    public Task HandleAsync(LibraryChangeBatch batch, CancellationToken cancellationToken)
    {
        _received.Enqueue(new Received(batch, _library?.IsScanRunning ?? false, cancellationToken));
        return OnHandle?.Invoke(batch, cancellationToken) ?? Task.CompletedTask;
    }

    public async Task<LibraryChangeBatch> WaitForBatchAsync(int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (_received.Count < count)
        {
            Assert.True(DateTime.UtcNow < deadline, $"{Name} didn't get batch {count}.");
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        return Batches[count - 1];
    }
}

/// <summary>A batch a <see cref="RecordingConsumer"/> received.</summary>
/// <param name="Batch">The batch.</param>
/// <param name="ScanRunning">Whether <c>IsScanRunning</c> was true when it arrived.</param>
/// <param name="Token">The token it came with.</param>
internal sealed record Received(LibraryChangeBatch Batch, bool ScanRunning, CancellationToken Token);
