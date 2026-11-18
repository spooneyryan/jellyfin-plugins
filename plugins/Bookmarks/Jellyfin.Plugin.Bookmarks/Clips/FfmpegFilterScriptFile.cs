using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// The filter graph of a playlist export (since 1.5.0 / 2.3.0), in a file of the system's temp folder that ffmpeg reads
/// (<c>-/filter_complex</c>): a graph of a hundred videos is too long for a command line. Deleted when disposed.
/// </summary>
/// <remarks>
/// The one file Bookmarks deletes outside <see cref="ClipFileOps"/>: its own, named after the export, in the temp folder,
/// never a clip file (FileOperationsCoverageTests names it).
/// </remarks>
internal sealed class FfmpegFilterScriptFile : IDisposable
{
    private readonly ILogger _logger;

    private FfmpegFilterScriptFile(string path, ILogger logger)
    {
        Path = path;
        _logger = logger;
    }

    /// <summary>
    /// Gets the file's path.
    /// </summary>
    public string Path { get; }

    /// <summary>
    /// Writes a filter graph to a new file of the temp folder.
    /// </summary>
    /// <param name="exportId">The export, which names the file.</param>
    /// <param name="graph">The filter graph.</param>
    /// <param name="logger">The logger, for a file that couldn't be deleted.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The file.</returns>
    public static async Task<FfmpegFilterScriptFile> WriteAsync(Guid exportId, string graph, ILogger logger, CancellationToken cancellationToken)
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "jfbm-export-" + exportId.ToString("N", CultureInfo.InvariantCulture) + ".txt");
        var file = new FfmpegFilterScriptFile(path, logger);
        try
        {
            await File.WriteAllTextAsync(path, graph, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            return file;
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Deletes the file.
    /// </summary>
    public void Dispose()
    {
        try
        {
            File.Delete(Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not delete {Path}", Path);
        }
    }
}
