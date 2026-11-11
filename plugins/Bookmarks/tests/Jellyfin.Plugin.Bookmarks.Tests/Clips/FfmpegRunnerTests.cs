using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bookmarks.Clips;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Clips;

/// <summary>
/// What <see cref="FfmpegRunner"/> keeps of a failed ffmpeg: the exit code, and the error lines the free-space guard
/// reads. A stand-in "ffmpeg" (Windows PowerShell) prints the six lines measured on Windows of a write into a full disk,
/// verbatim, to stderr and exits with -28, as ffmpeg 7.1.3-Jellyfin did through the same C runtime path.
/// </summary>
public sealed class FfmpegRunnerTests
{
    // ffmpeg 7.1.3-Jellyfin's lines on a full disk, as measured on Windows, verbatim.
    private static readonly string[] _fullDiskLines =
    [
        "[aost#0:1/copy @ 000001cebf328640] Error submitting a packet to the muxer: No space left on device",
        "    Last message repeated 1 times",
        "[out#0/matroska @ 000001cebd615340] Error muxing a packet",
        "[out#0/matroska @ 000001cebd615340] Task finished with error code: -28 (No space left on device)",
        "[out#0/matroska @ 000001cebd615340] Terminating thread with return code -28 (No space left on device)",
        "[out#0/matroska @ 000001cebd615340] Error writing trailer: No space left on device",
        "[out#0/matroska @ 000001cebd615340] Error closing file: No space left on device",
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AFailedRun_KeepsTheExitCode_AndTheFullDiskLines_WhichTheGuardReadsAsAFullDisk()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The stand-in ffmpeg is Windows PowerShell.");
        var script = string.Join("; ", _fullDiskLines.Select(line => $"[Console]::Error.WriteLine('{line}')")) + "; exit -28";
        var runner = new FfmpegRunner(NullLogger<FfmpegRunner>.Instance);

        var failure = await Assert.ThrowsAsync<ClipExtractionException>(
            () => runner.RunAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", script], TimeSpan.FromSeconds(1), null, Ct));

        Assert.Equal(-28, failure.ExitCode);
        Assert.Equal("ffmpeg failed with exit code -28: " + string.Join(' ', _fullDiskLines.Select(l => l.Trim())), failure.Message);
        Assert.Equal("ffmpeg's exit code -28", ClipStorageGuard.DiskFullSignal(failure));
    }

    [Fact]
    public async Task AFailedRunWithAnotherErrno_KeepsItsExitCode_AndIsNoFullDiskByItself()
    {
        // The control: another errno prints the same lines with "Invalid argument" and exits with -22.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The stand-in ffmpeg is Windows PowerShell.");
        var lines = _fullDiskLines.Select(l => l.Replace("No space left on device", "Invalid argument", StringComparison.Ordinal).Replace("-28", "-22", StringComparison.Ordinal));
        var script = string.Join("; ", lines.Select(line => $"[Console]::Error.WriteLine('{line}')")) + "; exit -22";
        var runner = new FfmpegRunner(NullLogger<FfmpegRunner>.Instance);

        var failure = await Assert.ThrowsAsync<ClipExtractionException>(
            () => runner.RunAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", script], TimeSpan.FromSeconds(1), null, Ct));

        Assert.Equal(-22, failure.ExitCode);
        Assert.Null(ClipStorageGuard.DiskFullSignal(failure));
    }
}
