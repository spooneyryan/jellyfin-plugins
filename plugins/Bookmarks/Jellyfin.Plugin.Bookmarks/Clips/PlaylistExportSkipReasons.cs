namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// Why a playlist export leaves an entry out: the words the export dialog shows.
/// </summary>
internal static class PlaylistExportSkipReasons
{
    /// <summary>An entry whose item is gone from the library.</summary>
    public const string NotInLibrary = "Not in the library any more";

    /// <summary>An item the caller may not open (its name isn't shown).</summary>
    public const string NotAvailable = "Not available to you";

    /// <summary>Music, a photo, a book, a folder: anything but a video.</summary>
    public const string NotAVideo = "Not a video";

    /// <summary>A video the caller may not download (clips have the same rule).</summary>
    public const string NotDownloadable = "Can't be downloaded";

    /// <summary>A disc, an ISO, a stream or a <c>.strm</c> file.</summary>
    public const string NotAFile = "Not a regular video file (a disc, an ISO, a stream or a .strm)";

    /// <summary>A file that isn't there: offline, moved or deleted.</summary>
    public const string FileMissing = "Its file can't be found (offline, moved or deleted)";

    /// <summary>A file without a video stream.</summary>
    public const string NoVideoStream = "No video stream";

    /// <summary>A video Jellyfin hasn't measured yet.</summary>
    public const string LengthUnknown = "Its length isn't known yet (not scanned)";
}
