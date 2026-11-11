using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.TestSupport;

/// <summary>
/// Locates ffmpeg/ffprobe for integration tests: <c>BOOKMARKS_TEST_FFMPEG</c>, then a local Jellyfin install, then PATH.
/// </summary>
internal sealed record FfmpegTools(string Ffmpeg, string Ffprobe)
{
    private static readonly Lazy<FfmpegTools?> _instance = new(Find);

    /// <summary>
    /// Gets the tools, or skips the calling test when ffmpeg isn't available.
    /// </summary>
    public static FfmpegTools Require()
    {
        var tools = _instance.Value;
        if (tools is null)
        {
            Assert.Skip("ffmpeg not found; set BOOKMARKS_TEST_FFMPEG to run the ffmpeg integration tests.");
        }

        return tools;
    }

    public async Task RunAsync(IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var (exitCode, _, error) = await ExecuteAsync(Ffmpeg, arguments, cancellationToken);
        Assert.True(exitCode == 0, $"ffmpeg failed: {error}");
    }

    public async Task<ProbeResult> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        var (exitCode, output, error) = await ExecuteAsync(
            Ffprobe,
            ["-v", "error", "-show_entries", "format=duration:stream=codec_type,codec_name", "-of", "json", path],
            cancellationToken);
        Assert.True(exitCode == 0, $"ffprobe failed: {error}");

        using var json = JsonDocument.Parse(output);
        var duration = double.Parse(json.RootElement.GetProperty("format").GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
        var streams = json.RootElement.GetProperty("streams").EnumerateArray()
            .Select(s => (s.GetProperty("codec_type").GetString()!, s.GetProperty("codec_name").GetString()!))
            .ToList();
        return new ProbeResult(duration, streams);
    }

    /// <summary>
    /// Reads a file's title tag, as <c>ffprobe -show_entries format_tags=title</c> shows it, or <c>null</c>.
    /// </summary>
    public async Task<string?> ProbeTitleAsync(string path, CancellationToken cancellationToken)
    {
        var (exitCode, output, error) = await ExecuteAsync(
            Ffprobe,
            ["-v", "error", "-show_entries", "format_tags=title", "-of", "json", path],
            cancellationToken);
        Assert.True(exitCode == 0, $"ffprobe failed: {error}");

        using var json = JsonDocument.Parse(output);
        return json.RootElement.TryGetProperty("format", out var format)
            && format.TryGetProperty("tags", out var tags)
            && tags.EnumerateObject().FirstOrDefault(t => string.Equals(t.Name, "title", StringComparison.OrdinalIgnoreCase)) is { Value.ValueKind: JsonValueKind.String } title
                ? title.Value.GetString()
                : null;
    }

    private static FfmpegTools? Find()
    {
        var executable = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        var candidates = new List<string?>
        {
            Environment.GetEnvironmentVariable("BOOKMARKS_TEST_FFMPEG"),
            OperatingSystem.IsWindows() ? @"C:\ProgramData\Jellyfin\Server\ffmpeg.exe" : "/usr/lib/jellyfin-ffmpeg/ffmpeg"
        };
        candidates.AddRange((Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir, executable)));

        foreach (var ffmpeg in candidates)
        {
            if (string.IsNullOrEmpty(ffmpeg) || !File.Exists(ffmpeg))
            {
                continue;
            }

            var ffprobe = Path.Combine(Path.GetDirectoryName(ffmpeg)!, executable.Replace("ffmpeg", "ffprobe", StringComparison.Ordinal));
            if (File.Exists(ffprobe))
            {
                return new FfmpegTools(ffmpeg, ffprobe);
            }
        }

        return null;
    }

    private static async Task<(int ExitCode, string Output, string Error)> ExecuteAsync(
        string executable,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, await output, await error);
    }
}

internal sealed record ProbeResult(double Duration, IReadOnlyList<(string Type, string Codec)> Streams);
