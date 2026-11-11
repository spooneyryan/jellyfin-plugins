using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Kit.Interop;

/// <summary>
/// Jellyfin Enhanced's settings as its XML holds them (<c>Jellyfin.Plugin.JellyfinEnhanced.xml</c>, read-only): what JE
/// has in memory since it last saved them, which it does at every start and every save.
/// </summary>
/// <param name="Exists">Whether the file is there.</param>
/// <param name="Flags">The settings the kit reads (<see cref="JellyfinEnhancedDefaults.Flags"/>) that the file sets.</param>
/// <param name="Shortcuts">The file's shortcuts, in file order.</param>
/// <param name="Problem">Why the file couldn't be read, or null.</param>
internal sealed record JeSettings(bool Exists, IReadOnlyDictionary<string, bool> Flags, IReadOnlyList<JeShortcut> Shortcuts, string? Problem)
{
    /// <summary>Gets the settings when there is no file.</summary>
    public static JeSettings Missing { get; } = new(false, new Dictionary<string, bool>(StringComparer.Ordinal), [], null);

    /// <summary>
    /// Gets the text settings of <see cref="JellyfinEnhancedDefaults.PresenceOnly"/> the file sets to something other than
    /// blanks; their values are never read.
    /// </summary>
    public IReadOnlySet<string> Set { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Gets whether a text setting of <see cref="JellyfinEnhancedDefaults.PresenceOnly"/> is set.
    /// </summary>
    /// <param name="name">The setting.</param>
    /// <returns>Whether the file sets it to something other than blanks.</returns>
    public bool IsSet(string name) => Set.Contains(name);

    /// <summary>
    /// Gets a setting: the file's value, else JE's default.
    /// </summary>
    /// <param name="name">The setting, one of <see cref="JellyfinEnhancedDefaults.Flags"/>.</param>
    /// <returns>Its value.</returns>
    public bool Flag(string name)
        => Flags.TryGetValue(name, out var value) ? value : JellyfinEnhancedDefaults.Flags.GetValueOrDefault(name);
}
