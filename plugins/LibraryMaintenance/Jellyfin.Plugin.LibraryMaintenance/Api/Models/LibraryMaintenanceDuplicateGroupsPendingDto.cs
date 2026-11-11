namespace Jellyfin.Plugin.LibraryMaintenance.Api.Models;

/// <summary>
/// The answer of <c>GET /Groups</c> and <c>GET /Groups/{groupId}</c> while the server has no index yet (a 202): the
/// first build runs, and the page asks again.
/// </summary>
public sealed class LibraryMaintenanceDuplicateGroupsPendingDto
{
    /// <summary>
    /// Gets or sets a value indicating whether a build is running (false only when a library scan put it off).
    /// </summary>
    public bool Rebuilding { get; set; }

    /// <summary>
    /// Gets or sets how far the build got, 0 to 1, or null when none runs.
    /// </summary>
    public double? Progress { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a library scan is running.
    /// </summary>
    public bool ScanInProgress { get; set; }

    /// <summary>
    /// Gets or sets why the last build failed, a fixed sentence with no path in it, or null (a build that retries it runs).
    /// </summary>
    public string? LastError { get; set; }
}
