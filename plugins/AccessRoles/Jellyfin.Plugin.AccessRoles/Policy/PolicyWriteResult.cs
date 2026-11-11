using System;
using System.Collections.Generic;
using Jellyfin.Plugin.AccessRoles.Access;

namespace Jellyfin.Plugin.AccessRoles.Policy;

/// <summary>
/// The outcome of one policy write.
/// </summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Target">The library fields the write was for (for <see cref="PolicyWriteOutcome.Resynced"/>, the committed
/// ones it wrote back).</param>
internal sealed record PolicyWriteResult(PolicyWriteOutcome Outcome, LibraryFields Target)
{
    /// <summary>Gets the user's library fields before the write, as the fresh read found them, when it got that far.</summary>
    public LibraryFields? Before { get; init; }

    /// <summary>
    /// Gets the fields the re-read found changed: the first round's for a rebuilt write, the second's for an aborted one.
    /// </summary>
    public IReadOnlyList<string> Differences { get; init; } = [];

    /// <summary>Gets a value indicating whether the write was built a second time after the re-read found a change.</summary>
    public bool Rebuilt { get; init; }

    /// <summary>Gets the error of a failed read or write, for the log and Diagnostics.</summary>
    public string? Error { get; init; }

    /// <summary>
    /// Gets a value indicating whether <c>UpdatePolicyAsync</c> itself threw: on 10.11.5 the user manager's cache may then
    /// hold the new access while the database holds the old (<c>UserManager.cs:719-720</c>), so the write must be made again,
    /// from the committed row (<see cref="PolicyWriter.HasFailedSave"/> keeps it until a write succeeds).
    /// </summary>
    public bool SaveFailed { get; init; }

    /// <summary>Gets a value indicating whether this is the write made again after a failed save, in the same call.</summary>
    public bool RetriedAfterFailedSave { get; init; }

    /// <summary>Gets the error of the failed save the write was made again after, for the audit.</summary>
    public string? FailedSaveError { get; init; }

    /// <summary>
    /// Gets a value indicating whether the write was aborted because the native library fields weren't the ones the caller
    /// planned against (an edit since its read, the "expected before" check): nothing was written.
    /// </summary>
    public bool NativeChanged { get; init; }

    /// <summary>
    /// Gets the policy fields the write took from the committed row, not from the server's cache: after a failed save of
    /// ours, the ones a concurrent save had changed (an admin's Dashboard save); in another write, the ones the cache and the
    /// database disagreed on.
    /// </summary>
    public IReadOnlyList<string> CommittedFields { get; init; } = [];

    /// <summary>
    /// Gets a value indicating whether the write kept <c>IsDisabled</c> on although the committed row has it off: the server
    /// enforced the user as disabled (a lockout 10.11.5 never saved, or a disable whose save failed), and the writer never
    /// enables a user.
    /// </summary>
    public bool KeptDisabled { get; init; }

    /// <summary>
    /// Gets a value indicating whether the committed row couldn't be read, so a write that needs it (after a failed save, or
    /// <see cref="PolicyWriter.ResyncAsync"/>) wasn't made.
    /// </summary>
    public bool CommittedUnavailable { get; init; }

    /// <summary>Gets a value indicating whether the wait before the write again was cancelled (shutdown, an aborted request).</summary>
    public bool Cancelled { get; init; }
}
