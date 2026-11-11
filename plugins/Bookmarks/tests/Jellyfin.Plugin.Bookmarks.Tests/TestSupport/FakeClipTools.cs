using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bookmarks.Clips;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Bookmarks.Services;
using Jellyfin.Plugin.Kit.TestSupport;

namespace Jellyfin.Plugin.Bookmarks.Tests.TestSupport;

/// <summary>
/// A clip planner without a library: each job writes <c>clips/&lt;bookmark id&gt;-&lt;start ticks&gt;</c> with the mode's
/// extension under a temporary folder, or, with <see cref="NamedFiles"/>, the file name the real planner gives it. As the
/// real planner since 1.1, a name that is taken (by any file, the bookmark's own previous clip included, or by a
/// <c>.partial</c>) gets a <c> (2)</c> suffix, and the plan's clip folder is <c>clips</c>.
/// </summary>
internal sealed class FakeClipPlanner : IClipPlanner
{
    private readonly TempDirectory _directory;
    private readonly ConcurrentDictionary<Guid, string> _planned = new();
    private int _plans;

    public FakeClipPlanner(TempDirectory directory)
    {
        _directory = directory;
    }

    /// <summary>Gets or sets what planning throws, if anything.</summary>
    public Exception? Failure { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a remake plans the bookmark's previous clip file as its output, as the real
    /// planner did before 1.1 (it treated the previous clip's path as free); the worker's move then refuses to replace it.
    /// </summary>
    public bool ReusePreviousFile { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a remake plans the bookmark's previous clip file as its output to replace
    /// (<see cref="ClipPlan.ReplacesOwnFile"/>), as the real planner does since Bookmarks 1.2.1 when its name is the clip's.
    /// </summary>
    public bool ReplacePreviousFile { get; set; }

    /// <summary>Gets the clip folder of every plan.</summary>
    public string Root => _directory.Combine("clips");

    /// <summary>Gets or sets the ffmpeg the plans run (the fake runner ignores it; the real one runs it).</summary>
    public string EncoderPath { get; set; } = "ffmpeg";

    /// <summary>
    /// Gets or sets a change to every plan's command, for a real ffmpeg: the source file, its streams, the encoding.
    /// </summary>
    public Func<ClipCommand, ClipCommand>? AdjustCommand { get; set; }

    /// <summary>
    /// Gets or sets every plan's size estimate (what the free-space guard checks); none by default.
    /// </summary>
    public long? EstimatedBytes { get; set; }

    /// <summary>Gets how many plans were made.</summary>
    public int Plans => _plans;

    /// <summary>
    /// Gets or sets a value indicating whether a clip file is named as the real planner names it: the item's name, the
    /// bookmark's name or else its note, and the range (<see cref="ClipFileNaming.BuildFileName"/>).
    /// </summary>
    public bool NamedFiles { get; set; }

    /// <summary>
    /// Gets the output path of the bookmark's last planned job, or, before any, the name a job for its clip range and mode
    /// gets when it is free.
    /// </summary>
    /// <param name="bookmark">The bookmark with its clip.</param>
    /// <returns>The path.</returns>
    public string OutputPathFor(Bookmark bookmark)
        => _planned.TryGetValue(bookmark.Id, out var planned)
            ? planned
            : Path.Combine(Root, FileName(
                bookmark with { StartTicks = bookmark.Clip?.StartTicks ?? bookmark.StartTicks, EndTicks = bookmark.Clip?.EndTicks ?? bookmark.EndTicks },
                bookmark.Clip?.Mode ?? BookmarkClipMode.Accurate));

    public ClipPlan Plan(Bookmark bookmark, BookmarkClipMode mode)
    {
        Interlocked.Increment(ref _plans);
        if (Failure is not null)
        {
            throw Failure;
        }

        var replaces = ReplacePreviousFile && bookmark.Clip?.FilePath is { Length: > 0 };
        var output = (ReusePreviousFile || replaces) && bookmark.Clip?.FilePath is { } previous
            ? previous
            : ClipFileNaming.MakeUnique(Root, FileName(bookmark, mode), path => File.Exists(path) || File.Exists(path + ".partial"));
        _planned[bookmark.Id] = output;
        var duration = TimeSpan.FromTicks(bookmark.EndTicks!.Value - bookmark.StartTicks);
        var command = new ClipCommand
        {
            InputPath = "source.mkv",
            OutputPath = output + ".partial",
            Mode = mode,
            Start = TimeSpan.FromTicks(bookmark.StartTicks),
            Duration = duration
        };
        return new ClipPlan
        {
            EncoderPath = EncoderPath,
            OutputPath = output,
            TempPath = output + ".partial",
            Duration = duration,
            Root = Root,
            EstimatedBytes = EstimatedBytes,
            Command = AdjustCommand?.Invoke(command) ?? command,
            ReplacesOwnFile = replaces
        };
    }

    // The bookmark with the range of the job.
    private string FileName(Bookmark bookmark, BookmarkClipMode mode)
        => NamedFiles
            ? ClipFileNaming.BuildFileName(bookmark.ItemName, BookmarkNames.ClipFileLabel(bookmark), bookmark.StartTicks, bookmark.EndTicks ?? bookmark.StartTicks, mode)
            : $"{bookmark.Id:N}-{bookmark.StartTicks}{ClipFileNaming.GetExtension(mode)}";
}

/// <summary>
/// ffmpeg for the worker's tests: writes "partial" to the output, reports half-way, then waits (while held) and writes
/// "clip". It can fail, and it can finish although its job was cancelled (a cancellation that came too late).
/// </summary>
internal sealed class FakeFfmpegRunner : IFfmpegRunner
{
    private TaskCompletionSource _gate = CompletedGate();
    private TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _runs;
    private int _cancelled;

    /// <summary>Gets a task that completes when a run has written its partial file and reported progress.</summary>
    public TaskCompletionSource Started => _started;

    /// <summary>Gets or sets what the run throws once released, if anything.</summary>
    public Exception? Failure { get; set; }

    /// <summary>
    /// Gets or sets what the run throws once released, made from the file it writes (the <c>.partial</c> path, which ffmpeg's
    /// error output names), if <see cref="Failure"/> is not set.
    /// </summary>
    public Func<string, Exception>? FailureFor { get; set; }

    /// <summary>Gets or sets a value indicating whether the run goes on after its token is cancelled.</summary>
    public bool IgnoreCancellation { get; set; }

    /// <summary>Gets how many runs started.</summary>
    public int Runs => Volatile.Read(ref _runs);

    /// <summary>Gets how many runs saw their token cancelled while they were held.</summary>
    public int CancelledRuns => Volatile.Read(ref _cancelled);

    /// <summary>Makes the next runs wait for <see cref="Release"/>.</summary>
    public void HoldUntilReleased()
    {
        _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Lets held runs finish.</summary>
    public void Release() => _gate.TrySetResult();

    public async Task RunAsync(
        string ffmpegPath,
        IReadOnlyList<string> arguments,
        TimeSpan expectedDuration,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _runs);
        var output = arguments[^1]["file:".Length..];
        await File.WriteAllTextAsync(output, "partial", CancellationToken.None);
        progress?.Report(0.5);
        _started.TrySetResult();
        try
        {
            await (IgnoreCancellation ? _gate.Task : _gate.Task.WaitAsync(cancellationToken));
        }
        catch (OperationCanceledException)
        {
            Interlocked.Increment(ref _cancelled);
            throw;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            // Cancelled while held, and finishing anyway (IgnoreCancellation).
            Interlocked.Increment(ref _cancelled);
        }

        if ((Failure ?? FailureFor?.Invoke(output)) is { } failure)
        {
            throw failure;
        }

        await File.WriteAllTextAsync(output, "clip", CancellationToken.None);
    }

    private static TaskCompletionSource CompletedGate()
    {
        var gate = new TaskCompletionSource();
        gate.SetResult();
        return gate;
    }
}
