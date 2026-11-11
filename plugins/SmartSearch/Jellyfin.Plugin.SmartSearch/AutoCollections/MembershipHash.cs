using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace Jellyfin.Plugin.SmartSearch.AutoCollections;

/// <summary>The hash of a membership in order: <c>sha256:</c> and the hex of the ids' bytes.</summary>
internal static class MembershipHash
{
    /// <summary>Hashes ids in order.</summary>
    /// <param name="ids">The ids.</param>
    /// <returns>The hash.</returns>
    public static string Of(IEnumerable<Guid> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> bytes = stackalloc byte[16];
        foreach (var id in ids)
        {
            id.TryWriteBytes(bytes);
            hash.AppendData(bytes);
        }

        return "sha256:" + Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
