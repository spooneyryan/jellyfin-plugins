using System;
using Jellyfin.Plugin.AccessRoles.Policy;

namespace Jellyfin.Plugin.AccessRoles.Sync;

/// <summary>
/// What the optional mirror did for one user (<see cref="PreferenceMirrorSync"/>).
/// </summary>
/// <param name="UserId">The user.</param>
/// <param name="Name">The user's name.</param>
/// <param name="Action">What it did.</param>
/// <param name="Reason">Why (the audit's <c>Reason</c>).</param>
/// <param name="Write">The write, or <c>null</c> when none was made.</param>
internal sealed record MirrorResult(Guid UserId, string Name, MirrorAction Action, string Reason, PreferenceWriteResult? Write);
