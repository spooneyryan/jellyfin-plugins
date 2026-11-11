using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Jellyfin.Plugin.Kit.Sqlite;

/// <summary>
/// File-level helpers for SQLite databases that need no SQLite library: the sidecar files, the 100-byte
/// header and whether a file is held open. Nothing here touches Microsoft.Data.Sqlite, so DI-built code and Diagnostics
/// may call it freely.
/// </summary>
internal static class SqliteFiles
{
    /// <summary>The size of a SQLite database header.</summary>
    public const int HeaderSize = 100;

    private static readonly byte[] _magic = "SQLite format 3\0"u8.ToArray();

    /// <summary>
    /// Gets the suffixes of the files SQLite keeps next to a database: the write-ahead log, its shared-memory index and
    /// the rollback journal. A database file is only ever moved, copied or deleted together with them (a leftover WAL
    /// beside a different database file can corrupt it; sqlite.org/howtocorrupt.html §1.4, §2.5).
    /// </summary>
    public static IReadOnlyList<string> SidecarSuffixes { get; } = ["-wal", "-shm", "-journal"];

    /// <summary>
    /// Lists the sidecar files that exist next to a database file.
    /// </summary>
    /// <param name="path">The database file.</param>
    /// <returns>Their full paths.</returns>
    public static IReadOnlyList<string> ListSidecars(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return SidecarSuffixes.Select(suffix => path + suffix).Where(File.Exists).ToList();
    }

    /// <summary>
    /// Reads a database file's header without opening it with SQLite: the page size, the file format versions (bytes 18
    /// and 19: 1 for a rollback journal, 2 for WAL) and <c>user_version</c> (bytes 60-63). Committed changes still in a
    /// <c>-wal</c> aren't seen here.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <returns>The header; <c>null</c> when the file is missing, can't be read, is shorter than a header or isn't a SQLite database.</returns>
    public static SqliteHeader? ReadHeader(string path)
        => TryReadHeader(path, out var header) ? header : null;

    /// <summary>
    /// Reads a database file's header as <see cref="ReadHeader"/> does, telling a file that can't be read just now (in use,
    /// no access, a read error) from one that has no header.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="header">The header; <c>null</c> when the file is missing, shorter than a header or not a SQLite database.</param>
    /// <returns><c>false</c> when the file is there but couldn't be read.</returns>
    public static bool TryReadHeader(string path, out SqliteHeader? header)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        header = null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> bytes = stackalloc byte[HeaderSize];
            if (stream.ReadAtLeast(bytes, HeaderSize, throwOnEndOfStream: false) < HeaderSize || !bytes[.._magic.Length].SequenceEqual(_magic))
            {
                return true;
            }

            int pageSize = BinaryPrimitives.ReadUInt16BigEndian(bytes[16..]);
            header = new SqliteHeader(
                pageSize == 1 ? 65536 : pageSize,
                bytes[18],
                bytes[19],
                BinaryPrimitives.ReadInt32BigEndian(bytes[60..]));
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Tells whether any handle to a file is open, by opening it with <see cref="FileShare.None"/> (the check that the
    /// connection pool let the file go).
    /// </summary>
    /// <param name="path">The file.</param>
    /// <returns><c>free</c>, <c>missing</c> or <c>locked</c>.</returns>
    public static string TryOpenExclusive(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!File.Exists(path))
        {
            return "missing";
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return "free";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "locked";
        }
    }

    /// <summary>
    /// Deletes a file and its sidecars, the sidecars first: should one of them not go, or the process stop in between,
    /// the database is still there with what is left of them, never a <c>-wal</c> without its database, which a new
    /// database started at that path would replay. A file that is missing is fine.
    /// </summary>
    /// <param name="path">The database file.</param>
    /// <exception cref="IOException">A file is in use and couldn't be deleted.</exception>
    /// <exception cref="UnauthorizedAccessException">A file may not be deleted (read-only, or no access).</exception>
    public static void DeleteWithSidecars(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        foreach (var file in SidecarSuffixes.Select(suffix => path + suffix).Append(path))
        {
            File.Delete(file);
        }
    }
}
