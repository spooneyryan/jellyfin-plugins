namespace Jellyfin.Plugin.Kit.Sqlite;

/// <summary>
/// The parts of a SQLite database header the kit reads (<see cref="SqliteFiles.ReadHeader"/>).
/// </summary>
/// <param name="PageSize">The page size in bytes.</param>
/// <param name="WriteVersion">Byte 18: 1 for a rollback journal, 2 for WAL.</param>
/// <param name="ReadVersion">Byte 19: 1 for a rollback journal, 2 for WAL.</param>
/// <param name="UserVersion">Bytes 60-63, <c>PRAGMA user_version</c>.</param>
internal readonly record struct SqliteHeader(int PageSize, byte WriteVersion, byte ReadVersion, int UserVersion)
{
    /// <summary>Gets a value indicating whether the file is in WAL mode (bytes 18 and 19 are 2).</summary>
    public bool IsWal => WriteVersion == 2 && ReadVersion == 2;
}
