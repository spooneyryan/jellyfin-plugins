using System;

namespace Jellyfin.Plugin.Kit.Interop;

/// <summary>
/// A key combination somebody else acts on.
/// </summary>
/// <param name="Combo">The combination, in the kit's notation.</param>
/// <param name="Owner">Who acts on it.</param>
/// <param name="Description">What it does there, for the warning: "Jellyfin Enhanced's Bookmark Current Time".</param>
/// <param name="UserId">The user whose own Jellyfin Enhanced shortcuts set it, or <c>null</c> for everyone.</param>
/// <param name="IsDefault">Whether it is Jellyfin Enhanced's default, listed because JE isn't installed (a later install would take it).</param>
internal sealed record ShortcutReservation(string Combo, ShortcutOwner Owner, string Description, Guid? UserId, bool IsDefault);
