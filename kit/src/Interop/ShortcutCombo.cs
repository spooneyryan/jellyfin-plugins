using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Jellyfin.Plugin.Kit.Interop;

/// <summary>
/// Key combinations in the one notation the kit and its web <c>shortcuts</c> module share: the
/// modifiers <c>Shift+</c>, <c>Meta+</c>, <c>Ctrl+</c> and <c>Alt+</c> in that order, then the key, an ASCII letter
/// upper-cased (<c>Shift+B</c>, <c>B</c>, <c>/</c>, <c>ArrowLeft</c>), Shift named whenever it is held. It is how Jellyfin
/// Enhanced 12.8 to 12.11 build the combination of a key press and compare it, whole, with their configured keys
/// (<c>js/enhanced/events.js:31-36</c>), so <c>Shift+B</c> is not <c>B</c>. The space bar is <c>Space</c> here and
/// <c>" "</c> in Jellyfin Enhanced. Jellyfin Enhanced 12.12 builds its combination in another order and drops Shift on
/// a symbol, and reads its keys more loosely (<see cref="FromJellyfinEnhanced1212"/>), still comparing whole combinations,
/// so <c>Shift+B</c> is still not <c>B</c>.
/// </summary>
/// <remarks>
/// <see cref="TryNormalize"/> reads what an admin or a user types (any case, any modifier order, the usual aliases);
/// <see cref="FromJellyfinEnhanced"/> reads a key from Jellyfin Enhanced's settings exactly as the 12.8 to 12.11 listener
/// compares it, <see cref="FromJellyfinEnhanced1212"/> as 12.12's does, and <see cref="JellyfinEnhancedCombos"/> gives both,
/// which is what the shortcut policy and the switch-off checklist use, so they hold whichever of them the server runs (an
/// update of Jellyfin Enhanced takes effect at a restart). <c>kit/web/kit.js</c> (<c>kit.shortcuts.normalize</c>) follows the
/// same rules as <see cref="TryNormalize"/>; <c>kit/testdata/shortcuts/combos.json</c> holds the cases both are tested
/// against, and Jellyfin Enhanced's readings.
/// </remarks>
internal static class ShortcutCombo
{
    /// <summary>The longest combination text read.</summary>
    public const int MaxLength = 40;

    /// <summary>The modifiers, in the order a combination names them.</summary>
    public static readonly IReadOnlyList<string> Modifiers = ["Shift", "Meta", "Ctrl", "Alt"];

    // KeyboardEvent.key values of keys that aren't one character (UI Events, "Key values"), in their canonical spelling,
    // which is what a combination names. Only these count as a key a keyboard sends, next to single characters.
    private static readonly FrozenSet<string> _namedKeys = new[]
    {
        "Enter", "Escape", "Space", "Tab", "Backspace", "Delete", "Insert", "Home", "End", "PageUp", "PageDown",
        "ArrowUp", "ArrowDown", "ArrowLeft", "ArrowRight", "ContextMenu", "PrintScreen", "ScrollLock", "Pause",
        "MediaPlayPause", "MediaPlay", "MediaPause", "MediaStop", "MediaTrackNext", "MediaTrackPrevious",
        "MediaFastForward", "MediaRewind", "AudioVolumeUp", "AudioVolumeDown", "AudioVolumeMute",
        "BrowserBack", "BrowserForward", "BrowserHome", "BrowserSearch"
    }.Concat(Enumerable.Range(1, 24).Select(n => "F" + n.ToString(CultureInfo.InvariantCulture))).ToFrozenSet(StringComparer.Ordinal);

    // What people type for a named key, case-insensitively.
    private static readonly FrozenDictionary<string, string> _aliases = BuildAliases();

    private static readonly FrozenDictionary<string, string> _modifierAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Shift"] = "Shift",
        ["Meta"] = "Meta",
        ["Cmd"] = "Meta",
        ["Command"] = "Meta",
        ["Win"] = "Meta",
        ["Windows"] = "Meta",
        ["Super"] = "Meta",
        ["Ctrl"] = "Ctrl",
        ["Control"] = "Ctrl",
        ["Alt"] = "Alt",
        ["Option"] = "Alt",
        ["Opt"] = "Alt"
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Reads a combination someone typed: modifiers in any order and case (<c>ctrl</c>, <c>Control</c>, <c>Cmd</c> and
    /// <c>Option</c> too), then one key: a single character (an ASCII letter upper-cased) or a named key
    /// (<c>Enter</c>, <c>Esc</c>, <c>Space</c>, <c>Up</c>, <c>PgDn</c>, <c>F5</c>...).
    /// </summary>
    /// <param name="text">The text; null, empty or white space means "no shortcut".</param>
    /// <param name="combo">The combination in the canonical notation (<c>Shift+Ctrl+B</c>), or <c>""</c> for no shortcut.</param>
    /// <returns>Whether the text is a combination (or empty); false for anything else, such as <c>Shift+</c> alone, two
    /// keys or an unknown key name.</returns>
    public static bool TryNormalize(string? text, out string combo)
    {
        combo = string.Empty;
        var trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return true;
        }

        if (trimmed.Length > MaxLength)
        {
            return false;
        }

        var modifiers = new HashSet<string>(StringComparer.Ordinal);
        var rest = trimmed;
        while (true)
        {
            var plus = rest.IndexOf('+', StringComparison.Ordinal);
            if (plus <= 0 || !_modifierAliases.TryGetValue(rest[..plus].Trim(), out var modifier))
            {
                break;
            }

            modifiers.Add(modifier);
            rest = rest[(plus + 1)..].TrimStart();
        }

        var key = NormalizeKey(rest.TrimEnd());
        if (key is null)
        {
            return false;
        }

        combo = Compose(modifiers, key);
        return true;
    }

    /// <summary>
    /// Normalizes a combination, or returns <c>null</c> when it isn't one (see <see cref="TryNormalize"/>).
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>The canonical combination, <c>""</c> for none, or <c>null</c>.</returns>
    public static string? Normalize(string? text) => TryNormalize(text, out var combo) ? combo : null;

    /// <summary>
    /// Reads a key from Jellyfin Enhanced's settings (its XML or a user's <c>shortcuts.json</c>) as the combination its
    /// listener compares it with (JE 12.8 to 12.11 <c>events.js:31-36</c>): the modifiers exactly as it writes them
    /// (<c>Shift+Meta+Ctrl+Alt+</c>, in that order and case), then the key, where only an upper-case ASCII letter can match
    /// a letter and <c>" "</c> is the space bar. Any other text is compared as it is, so it is returned as it is. JE 12.12
    /// reads keys more loosely: <see cref="FromJellyfinEnhanced1212"/>, and both at once: <see cref="JellyfinEnhancedCombos"/>.
    /// </summary>
    /// <param name="key">The configured key.</param>
    /// <returns>The combination in the kit's notation, or <c>null</c> when no key press can ever equal it (empty, a
    /// lower-case letter, modifiers out of order or alone).</returns>
    public static string? FromJellyfinEnhanced(string? key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > 4 * MaxLength)
        {
            return null;
        }

        var modifiers = new List<string>();
        var rest = key;
        foreach (var modifier in Modifiers)
        {
            var prefix = modifier + "+";
            if (rest.Length > prefix.Length && rest.StartsWith(prefix, StringComparison.Ordinal))
            {
                modifiers.Add(modifier);
                rest = rest[prefix.Length..];
            }
        }

        if (rest.Length == 1 && char.IsAsciiLetterLower(rest[0]))
        {
            return null;
        }

        if (rest == " ")
        {
            rest = "Space";
        }

        // A modifier name as the key, or a modifier that didn't come in JE's order: JE's combination always has the
        // modifiers first and in order, and never names a modifier key itself.
        var plus = rest.IndexOf('+', StringComparison.Ordinal);
        if (_modifierAliases.ContainsKey(rest) || (plus > 0 && _modifierAliases.ContainsKey(rest[..plus])))
        {
            return null;
        }

        return Compose(modifiers, rest);
    }

    /// <summary>
    /// Reads a key from Jellyfin Enhanced's settings (its XML or a user's <c>shortcuts.json</c>) as JE 12.12's listener
    /// compares it, and gives the key presses it acts on, in the kit's notation. JE 12.12 first drops a <c>Shift+</c> right
    /// before a last character that isn't a letter, a digit, white space or one of <c>, . / ; ' [ ] \ = ` -</c>
    /// (<c>config.js</c>, <c>normalizeShortcutKey</c>: <c>Shift++</c> is <c>+</c>), then reads the modifiers <c>Meta+</c>,
    /// <c>Ctrl+</c>, <c>Alt+</c> and <c>Shift+</c> in any order, exactly in that case, and a single ASCII letter in either
    /// case (<c>events.js</c>, <c>canonicalCombo</c>: <c>b</c> is <c>B</c>, <c>Ctrl+Shift+S</c> is <c>Shift+Ctrl+S</c>). A
    /// key press's combination drops Shift when its key is one character other than a letter or the space bar
    /// (<c>config.js</c>, <c>keyCombo</c>), so such a key is pressed with or without Shift, and never matches a key
    /// configured with Shift.
    /// </summary>
    /// <param name="key">The configured key.</param>
    /// <returns>The combinations of the key presses JE 12.12 acts on, in the kit's notation; empty when no key press can
    /// equal the key (empty, or a symbol configured with Shift). A key that is no key a keyboard sends (<c>Disabled</c>) is
    /// returned as it reads, as <see cref="FromJellyfinEnhanced"/> does: <see cref="IsPressable"/> tells.</returns>
    public static IReadOnlyList<string> FromJellyfinEnhanced1212(string? key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > 4 * MaxLength)
        {
            return [];
        }

        // normalizeShortcutKey: /Shift\+([^A-Za-z0-9\s,./;'\[\]\\=`-])$/ -> '$1' (one UTF-16 unit, the very end).
        var text = key;
        if (text.Length >= 7 && text.AsSpan(text.Length - 7, 6).SequenceEqual("Shift+") && !KeptAfterShift(text[^1]))
        {
            text = text[..^7] + text[^1];
        }

        // canonicalCombo: /^(Meta|Ctrl|Alt|Shift)\+(?=.)/ repeatedly, then a single ASCII letter upper-cased.
        var modifiers = new HashSet<string>(StringComparer.Ordinal);
        var rest = text;
        var matched = true;
        while (matched)
        {
            matched = false;
            foreach (var modifier in Modifiers)
            {
                var prefix = modifier + "+";
                if (rest.Length > prefix.Length && rest.StartsWith(prefix, StringComparison.Ordinal) && !IsJsLineTerminator(rest[prefix.Length]))
                {
                    modifiers.Add(modifier);
                    rest = rest[prefix.Length..];
                    matched = true;
                    break;
                }
            }
        }

        if (rest.Length == 1 && char.IsAsciiLetter(rest[0]))
        {
            return [Compose(modifiers, rest.ToUpperInvariant())];
        }

        if (rest == " ")
        {
            return [Compose(modifiers, "Space")];
        }

        if (rest.Length == 1)
        {
            // keyCombo drops Shift for a one-character key that is neither a letter nor the space bar.
            if (modifiers.Contains("Shift"))
            {
                return [];
            }

            var shifted = new HashSet<string>(modifiers, StringComparer.Ordinal) { "Shift" };
            return [Compose(modifiers, rest), Compose(shifted, rest)];
        }

        return [Compose(modifiers, rest)];
    }

    /// <summary>
    /// Gets the key presses Jellyfin Enhanced 12.8 to 12.12 act on for a configured key, in the kit's notation: the 12.8 to
    /// 12.11 reading (<see cref="FromJellyfinEnhanced"/>) and the 12.12 one (<see cref="FromJellyfinEnhanced1212"/>)
    /// together, each once, so a reservation or a check holds whichever of them the server runs.
    /// </summary>
    /// <param name="key">The configured key.</param>
    /// <returns>The combinations, the 12.11 reading first; empty when no version's key press can equal the key. Some may not
    /// be keys a keyboard sends (<see cref="IsPressable"/>).</returns>
    public static IReadOnlyList<string> JellyfinEnhancedCombos(string? key)
    {
        var combos = new List<string>();
        if (FromJellyfinEnhanced(key) is { } exact)
        {
            combos.Add(exact);
        }

        foreach (var combo in FromJellyfinEnhanced1212(key))
        {
            if (!combos.Contains(combo, StringComparer.Ordinal))
            {
                combos.Add(combo);
            }
        }

        return combos;
    }

    /// <summary>
    /// Gets a value indicating whether a keyboard can send the combination's key: a single character or a named key
    /// (<c>Enter</c>, <c>ArrowLeft</c>, <c>F5</c>, <c>MediaPlayPause</c>...). A key such as <c>Disabled</c>, which an admin
    /// can type into Jellyfin Enhanced's settings to switch a shortcut off for good, is not one.
    /// </summary>
    /// <param name="combo">A combination in the kit's notation.</param>
    /// <returns>Whether the key exists.</returns>
    public static bool IsPressable(string? combo)
    {
        var key = KeyOf(combo);
        return key is not null && (new StringInfo(key).LengthInTextElements == 1 || _namedKeys.Contains(key));
    }

    /// <summary>
    /// Gets the key of a combination in the kit's notation, without its modifiers.
    /// </summary>
    /// <param name="combo">The combination.</param>
    /// <returns>The key, or <c>null</c> for an empty combination.</returns>
    public static string? KeyOf(string? combo)
    {
        if (string.IsNullOrEmpty(combo))
        {
            return null;
        }

        var rest = combo;
        foreach (var modifier in Modifiers)
        {
            var prefix = modifier + "+";
            if (rest.Length > prefix.Length && rest.StartsWith(prefix, StringComparison.Ordinal))
            {
                rest = rest[prefix.Length..];
            }
        }

        return rest;
    }

    private static string? NormalizeKey(string key)
    {
        if (key.Length == 0)
        {
            return null;
        }

        if (new StringInfo(key).LengthInTextElements == 1)
        {
            if (char.IsWhiteSpace(key[0]) || char.IsControl(key[0]))
            {
                return null;
            }

            return key.Length == 1 && char.IsAsciiLetter(key[0]) ? key.ToUpperInvariant() : key;
        }

        if (_modifierAliases.ContainsKey(key))
        {
            return null;
        }

        return _aliases.TryGetValue(key, out var named) ? named : null;
    }

    // The last characters JE 12.12's normalizeShortcutKey keeps a Shift+ before: [A-Za-z0-9\s,./;'\[\]\\=`-].
    private static bool KeptAfterShift(char c)
        => char.IsAsciiLetterOrDigit(c) || IsJsWhiteSpace(c) || ",./;'[]\\=`-".Contains(c, StringComparison.Ordinal);

    // JavaScript's \s: Unicode white space and U+FEFF, but not U+0085, which .NET counts.
    private static bool IsJsWhiteSpace(char c) => c == (char)0xFEFF || (char.IsWhiteSpace(c) && c != (char)0x85);

    // What JavaScript's "." doesn't match (no s flag): the line terminators.
    private static bool IsJsLineTerminator(char c) => c == (char)10 || c == (char)13 || c == (char)0x2028 || c == (char)0x2029;

    private static string Compose(IEnumerable<string> modifiers, string key)
    {
        var set = modifiers as ICollection<string> ?? modifiers.ToList();
        var text = new StringBuilder();
        foreach (var modifier in Modifiers)
        {
            if (set.Contains(modifier))
            {
                text.Append(modifier).Append('+');
            }
        }

        return text.Append(key).ToString();
    }

    private static FrozenDictionary<string, string> BuildAliases()
    {
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in _namedKeys)
        {
            aliases[key] = key;
        }

        aliases["Esc"] = "Escape";
        aliases["Return"] = "Enter";
        aliases["Spacebar"] = "Space";
        aliases["Up"] = "ArrowUp";
        aliases["Down"] = "ArrowDown";
        aliases["Left"] = "ArrowLeft";
        aliases["Right"] = "ArrowRight";
        aliases["Del"] = "Delete";
        aliases["Ins"] = "Insert";
        aliases["PgUp"] = "PageUp";
        aliases["PgDn"] = "PageDown";
        aliases["Page Up"] = "PageUp";
        aliases["Page Down"] = "PageDown";
        return aliases.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }
}
