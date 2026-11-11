using System.Collections.Generic;

namespace Jellyfin.Plugin.AccessRoles.Policy;

/// <summary>
/// What a write of a user's native <c>EnableUserPreferenceAccess</c> did (<see cref="PolicyWriter.WritePreferenceAccessAsync"/>;
/// the optional mirror, off by default).
/// </summary>
/// <param name="Outcome">Written, unchanged (the flag already was the value), or why nothing was written.</param>
/// <param name="Before">The flag the server enforced before, or <c>null</c> when it couldn't be read.</param>
internal sealed record PreferenceWriteResult(PolicyWriteOutcome Outcome, bool? Before)
{
    /// <summary>Gets a value indicating whether the server enforces the value right after the write (or already did).</summary>
    public bool Holds { get; init; }

    /// <summary>Gets a value indicating whether the flag wasn't the one the caller found (nothing written).</summary>
    public bool NativeChanged { get; init; }

    /// <summary>Gets a value indicating whether the save itself failed (the cache may hold our entity).</summary>
    public bool SaveFailed { get; init; }

    /// <summary>Gets the error of a failed read or write, or <c>null</c>.</summary>
    public string? Error { get; init; }

    /// <summary>Gets the fields that changed while the write was built, when it was aborted for them.</summary>
    public IReadOnlyList<string> Differences { get; init; } = [];

    /// <summary>Gets a value indicating whether the server holds the value now: written and holding, or unchanged.</summary>
    public bool Done => Outcome is PolicyWriteOutcome.Written or PolicyWriteOutcome.Unchanged && Holds;
}
