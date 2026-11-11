using System;
using System.Collections.Generic;
using Jellyfin.Plugin.SmartSearch.Query.Evaluation;

namespace Jellyfin.Plugin.SmartSearch.Query.Planning;

/// <summary>Compares text by its folded (clean) form, as core compares pushed genres and tags with <c>CleanValue</c>.</summary>
internal sealed class FoldComparer : IEqualityComparer<string>
{
    /// <summary>Gets the instance.</summary>
    public static FoldComparer Instance { get; } = new();

    /// <inheritdoc />
    public bool Equals(string? x, string? y)
        => x is null || y is null ? x is null && y is null : string.Equals(RuleValues.Fold(x), RuleValues.Fold(y), StringComparison.Ordinal);

    /// <inheritdoc />
    public int GetHashCode(string obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        return RuleValues.Fold(obj).GetHashCode(StringComparison.Ordinal);
    }
}
