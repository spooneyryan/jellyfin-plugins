using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Kit.Interop;

/// <summary>
/// The result of checking a plugin's shortcut setting (<see cref="ShortcutPolicy.Check"/>).
/// </summary>
/// <param name="Name">The setting's name.</param>
/// <param name="Label">What the shortcut does.</param>
/// <param name="Combo">The combination in the kit's notation, <c>""</c> for off, or <c>null</c> when the text isn't one.</param>
/// <param name="IsValid">Whether the text is a combination (or empty).</param>
/// <param name="Problem">Why it isn't, or <c>null</c>.</param>
/// <param name="Reservations">Who else acts on the combination; empty when it is free.</param>
internal sealed record ShortcutCheck(string Name, string Label, string? Combo, bool IsValid, string? Problem, IReadOnlyList<ShortcutReservation> Reservations)
{
    /// <summary>Gets a value indicating whether somebody else acts on the combination.</summary>
    public bool IsReserved => Reservations.Count > 0;

    /// <summary>
    /// Gets the sentence for the settings page: the problem, the reservation, or <c>null</c> when the shortcut is fine.
    /// </summary>
    public string? Warning
    {
        get
        {
            if (!IsValid)
            {
                return Problem;
            }

            if (Reservations.Count == 0)
            {
                return null;
            }

            var owners = Reservations.Select(r => r.Description).Distinct(StringComparer.Ordinal).ToList();
            var who = owners.Count == 1 ? owners[0] : string.Join(", ", owners.Take(owners.Count - 1)) + " and " + owners[^1];
            var onlyDefaults = Reservations.All(r => r.IsDefault);
            return onlyDefaults
                ? $"{Combo} is {who} by default, so {Label} would stop working if Jellyfin Enhanced is installed."
                : $"{Combo} is taken by {who}, so {Label} won't work with it. Choose another key.";
        }
    }
}
