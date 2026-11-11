using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Kit.Interop;

/// <summary>
/// How Jellyfin Enhanced turns its settings into the keys its web script compares, in the versions the kit was tested
/// with, 12.8 to 12.12 (read from JE's source and IL, and checked in the <c>je</c> sandbox against 12.11 and 12.12):
/// <list type="bullet">
/// <item><description>Its script reduces the server's <c>Shortcuts</c> list to one key per action, the <b>last</b> entry of
/// a name winning (<c>config.js</c>, <c>JE.initializeShortcuts</c>), and lays each user's own list over it the same way;
/// an entry whose key is <c>""</c> (or <c>null</c>) switches the action off, because no key press builds an empty
/// combination. 12.12 then reads each key more loosely than 12.11 (<see cref="ShortcutCombo.FromJellyfinEnhanced1212"/>:
/// <c>b</c> is <c>B</c>, modifiers in any order); the kit takes both readings (<see cref="ShortcutCombo.JellyfinEnhancedCombos"/>).</description></item>
/// <item><description>At every start JE's constructor normalises the server list (<c>BackfillMissingDefaultShortcuts</c>,
/// the same IL in 12.8, 12.11 and 12.12): it walks the list from the end, <b>drops every entry without a key as
/// malformed</b>, keeps the last entry with a key of each name (so of a duplicated name the last keyed entry wins,
/// wherever an empty one sits), and appends the default of every action left without a kept entry, which JE then saves.
/// So an admin's "Disable" (12.11's settings page writes <c>Key: ""</c> for its Disable button, 12.12's for Delete or the
/// block button on its key grid) lasts only until the next restart, after which the default key is active again; a key
/// that no key press can produce, such as the text <c>Disabled</c>, stays, and so does any other key. A user's own
/// <c>""</c> is never normalised, so it lasts.</description></item>
/// </list>
/// </summary>
internal static class JeShortcuts
{
    /// <summary>
    /// Gets the keys JE's script has in effect right now for the server's list (before any user's own): the last entry
    /// per action as the settings hold it; an action the file doesn't list has its default, which JE's start would have
    /// put back (the file is always normalised after a start).
    /// </summary>
    /// <param name="settings">JE's settings.</param>
    /// <returns>Action name to key; <c>""</c> for an action switched off.</returns>
    public static IReadOnlyDictionary<string, string> EffectiveKeys(JeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var keys = Defaults();
        foreach (var shortcut in settings.Shortcuts)
        {
            if (shortcut.Name.Length > 0)
            {
                keys[shortcut.Name] = shortcut.Key ?? string.Empty;
            }
        }

        return keys;
    }

    /// <summary>
    /// Gets the keys the server's list will hold after JE's next start normalises it, as its backfill computes them: of
    /// each name the last entry whose key isn't empty (entries without a key are dropped first, so <c>X</c> followed by
    /// <c>""</c> keeps <c>X</c>, not the default); an action with no keyed entry gets JE's default back.
    /// </summary>
    /// <param name="settings">JE's settings.</param>
    /// <returns>Action name to key.</returns>
    public static IReadOnlyDictionary<string, string> KeysAfterRestart(JeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var shortcut in settings.Shortcuts)
        {
            if (shortcut.Name.Length > 0 && !string.IsNullOrEmpty(shortcut.Key))
            {
                keys[shortcut.Name] = shortcut.Key;
            }
        }

        foreach (var shortcut in JellyfinEnhancedDefaults.Shortcuts)
        {
            keys.TryAdd(shortcut.Name, shortcut.Key ?? string.Empty);
        }

        return keys;
    }

    /// <summary>
    /// Gets a user's own keys, laid over the server's by JE's script: the last entry per action.
    /// </summary>
    /// <param name="user">The user's file.</param>
    /// <returns>Action name to key; <c>""</c> for an action the user switched off.</returns>
    public static IReadOnlyDictionary<string, string> UserOverrides(JeUserShortcuts user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var shortcut in user.Shortcuts)
        {
            if (shortcut.Name.Length > 0)
            {
                keys[shortcut.Name] = shortcut.Key ?? string.Empty;
            }
        }

        return keys;
    }

    /// <summary>
    /// Gets a value indicating whether a configured key leaves its action off in JE 12.8 to 12.12: empty, or text no key
    /// press produces in either reading (<c>Disabled</c>, modifiers alone; a lower-case letter is off for 12.11 only, since
    /// 12.12 reads it as the letter, so it doesn't count as off).
    /// </summary>
    /// <param name="key">The key as JE's settings hold it.</param>
    /// <returns>Whether no key press triggers the action.</returns>
    public static bool IsOff(string? key)
    {
        foreach (var combo in ShortcutCombo.JellyfinEnhancedCombos(key))
        {
            if (ShortcutCombo.IsPressable(combo))
            {
                return false;
            }
        }

        return true;
    }

    private static Dictionary<string, string> Defaults()
    {
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var shortcut in JellyfinEnhancedDefaults.Shortcuts)
        {
            keys[shortcut.Name] = shortcut.Key ?? string.Empty;
        }

        return keys;
    }
}
