using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Jellyfin.Plugin.Kit.Interop;

/// <summary>
/// Which key combinations our plugins' shortcuts may not use: jellyfin-web's video OSD keys, as
/// its player chunk compares them (modifier-sensitive), and Jellyfin Enhanced's configured keys, read from
/// its settings and every user's own shortcuts on every call (<see cref="JellyfinEnhancedReader.Read"/>, memoised briefly),
/// or its defaults while it isn't installed, each as JE 12.8 to 12.11 and as JE 12.12 read it
/// (<see cref="ShortcutCombo.JellyfinEnhancedCombos"/>). <see cref="Check"/> validates a plugin's shortcut setting against them, for the
/// warning on its settings page; <see cref="ReservedCombos"/> is what a plugin's capabilities list for one user's browser
/// (the server-wide keys and that user's own JE overrides, never another user's).
/// </summary>
internal sealed class ShortcutPolicy
{
    /// <summary>
    /// jellyfin-web 10.11.5's video OSD keys (the <c>playback-video</c> chunk's keydown switch): <c>K L J F M G H</c>,
    /// the arrows, Home, End, PageUp and PageDown act only without Shift; <c>P</c> and <c>N</c> only with Shift (previous
    /// and next track); <c>0-9 &lt; &gt;</c>, Enter, Escape and Space with any Shift state; Ctrl, Alt and Meta combinations
    /// never.
    /// </summary>
    public static readonly IReadOnlyList<ShortcutReservation> JellyfinWeb = BuildJellyfinWeb();

    private const string JellyfinWebDescription = "jellyfin-web's video player";

    private readonly JellyfinEnhancedReader _reader;

    /// <summary>
    /// Initializes a new instance of the <see cref="ShortcutPolicy"/> class.
    /// </summary>
    /// <param name="reader">The Jellyfin Enhanced reader.</param>
    public ShortcutPolicy(JellyfinEnhancedReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        _reader = reader;
    }

    /// <summary>
    /// Lists every reservation: jellyfin-web's, then Jellyfin Enhanced's for the server's keys and for each user's own.
    /// </summary>
    /// <param name="snapshot">What the reader found; read now when <c>null</c>.</param>
    /// <returns>The reservations, in that order; a combination may appear more than once (two owners).</returns>
    public IReadOnlyList<ShortcutReservation> Reserved(JellyfinEnhancedSnapshot? snapshot = null)
    {
        snapshot ??= _reader.Read();
        var reservations = new List<ShortcutReservation>(JellyfinWeb);
        reservations.AddRange(JellyfinEnhanced(snapshot));
        return reservations;
    }

    /// <summary>
    /// Lists the reserved combinations one user's browser needs, each once, for a plugin's capabilities
    /// (<c>ReservedShortcuts</c>): jellyfin-web's, Jellyfin Enhanced's server-wide keys, and that user's own overrides
    /// only. Another user's personal JE shortcuts are never in it (the kit's privacy rule: nothing a non-admin can call
    /// reveals other users' data); the admin's Diagnostics has <see cref="AllReservedCombos"/>.
    /// </summary>
    /// <param name="userId">The caller.</param>
    /// <param name="snapshot">What the reader found; read now when <c>null</c>.</param>
    /// <returns>The combinations, in the order they were reserved.</returns>
    public IReadOnlyList<string> ReservedCombos(Guid userId, JellyfinEnhancedSnapshot? snapshot = null)
        => Distinct(Reserved(snapshot).Where(r => r.UserId is null || r.UserId == userId));

    /// <summary>
    /// Lists every reserved combination, each once, every user's own included: for the admin's Diagnostics entry
    /// (<c>RequiresElevation</c>), never for a capabilities response.
    /// </summary>
    /// <param name="snapshot">What the reader found; read now when <c>null</c>.</param>
    /// <returns>The combinations, in the order they were reserved.</returns>
    public IReadOnlyList<string> AllReservedCombos(JellyfinEnhancedSnapshot? snapshot = null)
        => Distinct(Reserved(snapshot));

    private static List<string> Distinct(IEnumerable<ShortcutReservation> reservations)
        => reservations.Select(r => r.Combo).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>
    /// Checks a plugin's shortcut setting: normalises what the admin typed and lists who reserves it.
    /// </summary>
    /// <param name="setting">The setting.</param>
    /// <param name="snapshot">What the reader found; read now when <c>null</c>.</param>
    /// <returns>The check.</returns>
    public ShortcutCheck Check(JeShortcutSetting setting, JellyfinEnhancedSnapshot? snapshot = null)
    {
        ArgumentNullException.ThrowIfNull(setting);
        if (!ShortcutCombo.TryNormalize(setting.Combo, out var combo))
        {
            return new ShortcutCheck(setting.Name, setting.Label, null, false, $"\"{setting.Combo?.Trim()}\" isn't a key combination; use a key with optional modifiers, such as Shift+B.", []);
        }

        if (combo.Length == 0)
        {
            return new ShortcutCheck(setting.Name, setting.Label, string.Empty, true, null, []);
        }

        var reservations = Reserved(snapshot).Where(r => string.Equals(r.Combo, combo, StringComparison.Ordinal)).ToList();
        return new ShortcutCheck(setting.Name, setting.Label, combo, true, null, reservations);
    }

    /// <summary>
    /// Lists Jellyfin Enhanced's reservations: with JE installed, the server's effective keys (the admin's, whether JE is
    /// loaded or waits for a restart) and each user's own overrides; without JE, its defaults, so a plugin's default
    /// binding stays free if JE is installed later.
    /// </summary>
    /// <param name="snapshot">What the reader found.</param>
    /// <returns>The reservations.</returns>
    public static IReadOnlyList<ShortcutReservation> JellyfinEnhanced(JellyfinEnhancedSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var reservations = new List<ShortcutReservation>();
        var installed = snapshot.Plugin.Installed;
        var settings = installed ? snapshot.Settings : JeSettings.Missing;
        var disabledAll = installed && settings.Flag("DisableAllShortcuts");
        const string jeName = "Jellyfin Enhanced";

        // The help panel's key: its own listener, with or without a setting.
        foreach (var combo in JellyfinEnhancedDefaults.PanelCombos)
        {
            reservations.Add(new ShortcutReservation(combo, ShortcutOwner.JellyfinEnhanced, "Jellyfin Enhanced's help panel", null, !installed));
        }

        if (disabledAll)
        {
            return reservations;
        }

        foreach (var (name, key) in JeShortcuts.EffectiveKeys(settings))
        {
            AddJe(reservations, name, key, null, !installed, jeName);
        }

        if (installed)
        {
            foreach (var user in snapshot.Users)
            {
                foreach (var (name, key) in JeShortcuts.UserOverrides(user))
                {
                    AddJe(reservations, name, key, user.UserId, false, jeName);
                }
            }
        }

        return reservations;
    }

    private static void AddJe(List<ShortcutReservation> reservations, string name, string key, Guid? userId, bool isDefault, string owner)
    {
        var label = JellyfinEnhancedDefaults.LabelOf(name);
        var description = userId is null ? $"{owner}'s {label}" : $"{owner}'s {label}, for one user";
        if (string.Equals(name, JellyfinEnhancedFiles.PercentageShortcut, StringComparison.Ordinal))
        {
            // Any value but "" makes the digit keys jump, with or without Shift (its check reads event.key, not the combination).
            if (key.Length > 0)
            {
                for (var digit = 0; digit <= 9; digit++)
                {
                    var text = digit.ToString(CultureInfo.InvariantCulture);
                    reservations.Add(new ShortcutReservation(text, ShortcutOwner.JellyfinEnhanced, description, userId, isDefault));
                    reservations.Add(new ShortcutReservation("Shift+" + text, ShortcutOwner.JellyfinEnhanced, description, userId, isDefault));
                }
            }

            return;
        }

        // The key presses JE 12.8 to 12.11 and 12.12 act on for the key, both (a JE update takes effect at a restart): 12.12
        // reads b as B and modifiers in any order, and takes a one-character symbol with or without Shift.
        foreach (var combo in ShortcutCombo.JellyfinEnhancedCombos(key))
        {
            if (ShortcutCombo.IsPressable(combo))
            {
                reservations.Add(new ShortcutReservation(combo, ShortcutOwner.JellyfinEnhanced, description, userId, isDefault));
            }
        }
    }

    private static List<ShortcutReservation> BuildJellyfinWeb()
    {
        var list = new List<ShortcutReservation>();
        void Add(string combo) => list.Add(new ShortcutReservation(combo, ShortcutOwner.JellyfinWeb, JellyfinWebDescription, null, false));

        foreach (var key in new[] { "K", "L", "J", "F", "M", "G", "H", "ArrowUp", "ArrowDown", "ArrowLeft", "ArrowRight", "Home", "End", "PageUp", "PageDown" })
        {
            Add(key);
        }

        Add("Shift+P");
        Add("Shift+N");
        foreach (var key in Enumerable.Range(0, 10).Select(d => d.ToString(CultureInfo.InvariantCulture)).Concat(["<", ">", "Enter", "Escape", "Space"]))
        {
            Add(key);
            Add("Shift+" + key);
        }

        return list;
    }
}
