using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Kit.Interop;
using Xunit;

namespace Jellyfin.Plugin.Kit.Tests.Interop;

/// <summary>
/// <see cref="JeShortcuts"/>: how Jellyfin Enhanced 12.8 to 12.11 turn the settings list and a user's own list into the
/// keys in effect, and what its start-up normalisation puts back.
/// </summary>
public sealed class JeShortcutsTests
{
    [Fact]
    public void TheLastEntryOfAName_Wins_AndMissingActions_HaveTheirDefault()
    {
        // XmlSerializer appends the file's rows after the constructor's defaults; JE's reduce keeps the last.
        var settings = new JeSettings(true, new Dictionary<string, bool>(StringComparer.Ordinal), [
            new("BookmarkCurrentTime", "B"),
            new("BookmarkCurrentTime", "Shift+Alt+B"),
            new("OpenSearch", "/"),
            new(string.Empty, "X")
        ], null);

        var keys = JeShortcuts.EffectiveKeys(settings);

        Assert.Equal("Shift+Alt+B", keys["BookmarkCurrentTime"]);
        Assert.Equal("/", keys["OpenSearch"]);
        Assert.Equal("0-9", keys["JumpToPercentage"]);
        Assert.Equal(20, keys.Count);
        Assert.DoesNotContain(string.Empty, keys.Keys);
    }

    [Fact]
    public void AnEmptyKey_IsOffNow_ButComesBackAtTheNextStart()
    {
        var settings = new JeSettings(true, new Dictionary<string, bool>(StringComparer.Ordinal), [new("BookmarkCurrentTime", string.Empty), new("SubtitleMenu", "Disabled")], null);

        Assert.Equal(string.Empty, JeShortcuts.EffectiveKeys(settings)["BookmarkCurrentTime"]);
        Assert.Equal("B", JeShortcuts.KeysAfterRestart(settings)["BookmarkCurrentTime"]);

        // Text no key press produces is kept as it is, so it stays off.
        Assert.Equal("Disabled", JeShortcuts.KeysAfterRestart(settings)["SubtitleMenu"]);
        Assert.True(JeShortcuts.IsOff(string.Empty));
        Assert.True(JeShortcuts.IsOff(null));
        Assert.True(JeShortcuts.IsOff("Disabled"));
        Assert.True(JeShortcuts.IsOff("Shift+"));
        Assert.False(JeShortcuts.IsOff("B"));
        Assert.False(JeShortcuts.IsOff("Shift+Alt+B"));
        Assert.False(JeShortcuts.IsOff(" "));

        // JE 12.12 reads keys more loosely than 12.11 (ShortcutCombo.FromJellyfinEnhanced1212): a lower-case letter is the
        // letter and the modifiers may come in any order, so these act there, and count as on whichever version runs.
        Assert.False(JeShortcuts.IsOff("b"));
        Assert.False(JeShortcuts.IsOff("Ctrl+Shift+S"));
        Assert.False(JeShortcuts.IsOff("Shift+/"), "12.12 never matches a symbol configured with Shift, but 12.11 does");
    }

    [Fact]
    public void TheBackfill_KeepsTheLastKeyedEntryOfAName_WhereverAnEmptyOneSits()
    {
        // JE's reverse walk drops entries without a key first, then keeps the last keyed entry per name (the same IL in
        // 12.8 and 12.11): X then "" restores X, not the default.
        var settings = new JeSettings(true, new Dictionary<string, bool>(StringComparer.Ordinal), [
            new("BookmarkCurrentTime", "X"),
            new("BookmarkCurrentTime", string.Empty),
            new("OpenSearch", string.Empty),
            new("OpenSearch", "Shift+/"),
            new("GoToHome", null),
            new("SubtitleMenu", "S"),
            new("SubtitleMenu", "Shift+S"),
            new(string.Empty, "Q")
        ], null);

        var now = JeShortcuts.EffectiveKeys(settings);
        var later = JeShortcuts.KeysAfterRestart(settings);

        Assert.Equal(string.Empty, now["BookmarkCurrentTime"]);
        Assert.Equal("X", later["BookmarkCurrentTime"]);
        Assert.Equal("Shift+/", later["OpenSearch"]);
        Assert.Equal("Shift+H", later["GoToHome"]);
        Assert.Equal("Shift+S", later["SubtitleMenu"]);
        Assert.Equal(20, later.Count);
        Assert.DoesNotContain(string.Empty, later.Keys);
    }

    [Fact]
    public void AUsersOwnList_IsReducedTheSameWay()
    {
        var user = new JeUserShortcuts(Guid.NewGuid(), [new("BookmarkCurrentTime", "B"), new("BookmarkCurrentTime", string.Empty), new("OpenSearch", "Shift+/")], null);

        var keys = JeShortcuts.UserOverrides(user);

        Assert.Equal(2, keys.Count);
        Assert.Equal(string.Empty, keys["BookmarkCurrentTime"]);
        Assert.Equal("Shift+/", keys["OpenSearch"]);
    }

    [Fact]
    public void WithoutASettingsFile_TheDefaultsApply()
    {
        var keys = JeShortcuts.EffectiveKeys(JeSettings.Missing);

        Assert.Equal("B", keys["BookmarkCurrentTime"]);
        Assert.Equal("Shift+H", keys["GoToHome"]);
        Assert.Equal(keys, JeShortcuts.KeysAfterRestart(JeSettings.Missing));
    }
}
