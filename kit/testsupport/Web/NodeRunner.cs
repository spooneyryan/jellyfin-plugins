using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Kit.TestSupport.Web;

/// <summary>
/// Runs Node.js (22, on the PATH) for the web script checks. Every caller skips its test when
/// <see cref="FindNode"/> returns <c>null</c>, as the Bookmarks ffmpeg tests do without ffmpeg.
/// </summary>
public static class NodeRunner
{
    /// <summary>
    /// Finds <c>node</c> (<c>node.exe</c> on Windows) on the PATH.
    /// </summary>
    /// <returns>The full path, or <c>null</c>.</returns>
    public static string? FindNode()
        => (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir.Trim('"'), OperatingSystem.IsWindows() ? "node.exe" : "node"))
            .FirstOrDefault(File.Exists);

    /// <summary>
    /// Runs node with arguments and extra environment variables, and waits for it.
    /// </summary>
    /// <param name="node">The node executable.</param>
    /// <param name="arguments">The arguments.</param>
    /// <param name="environment">Extra environment variables.</param>
    /// <param name="workingDirectory">The working directory, or <c>null</c> for the current one.</param>
    /// <param name="timeout">How long to wait (default two minutes); the process tree is killed after it.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The exit code and the output.</returns>
    public static async Task<NodeRun> RunAsync(
        string node,
        IEnumerable<string> arguments,
        IReadOnlyDictionary<string, string>? environment = null,
        string? workingDirectory = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var start = new ProcessStartInfo(node)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (workingDirectory is not null)
        {
            start.WorkingDirectory = workingDirectory;
        }

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in environment ?? new Dictionary<string, string>())
        {
            start.Environment[name] = value;
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errors = process.StandardError.ReadToEndAsync(cancellationToken);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout ?? TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        return new NodeRun(process.ExitCode, await output.ConfigureAwait(false), await errors.ConfigureAwait(false));
    }
}

/// <summary>
/// The result of a node run.
/// </summary>
/// <param name="ExitCode">The exit code.</param>
/// <param name="StandardOutput">What it wrote to stdout.</param>
/// <param name="StandardError">What it wrote to stderr.</param>
public sealed record NodeRun(int ExitCode, string StandardOutput, string StandardError)
{
    /// <summary>Gets both outputs, for assertion messages.</summary>
    public string Output => (StandardOutput + Environment.NewLine + StandardError).Trim();
}
