using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Kit.Interop;
using Jellyfin.Plugin.Kit.TestSupport;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Kit.Tests.Interop;

/// <summary>
/// <see cref="ShortcutPolicy"/>: jellyfin-web's OSD keys as combinations, Jellyfin
/// Enhanced's configured keys and each user's own, its defaults while it isn't installed, and the check of a plugin's
/// setting with the warning the settings page shows.
/// </summary>
public sealed class ShortcutPolicyTests
{
    private static readonly JeSettings Fixture = new(true, new Dictionary<string, bool>(StringComparer.Ordinal), [new("BookmarkCurrentTime", "B"), new("GoToHome", "Shift+H")], null);

    [Fact]
    public void JellyfinWebsKeys_AreModifierSensitiveCombos()
    {
        var combos = ShortcutPolicy.JellyfinWeb.Select(r => r.Combo).ToHashSet(StringComparer.Ordinal);

        Assert.Contains("K", combos);
        Assert.DoesNotContain("Shift+K", combos);
        Assert.Contains("Shift+P", combos);
        Assert.DoesNotContain("P", combos);
        Assert.Contains("ArrowLeft", combos);
        Assert.Contains("Space", combos);
        Assert.Contains("Shift+Space", combos);
        Assert.Contains("5", combos);
        Assert.Contains("Shift+5", combos);
        Assert.Contains("<", combos);
        Assert.DoesNotContain("B", combos);
        Assert.DoesNotContain("Shift+B", combos);
        Assert.All(ShortcutPolicy.JellyfinWeb, r => Assert.Equal(ShortcutOwner.JellyfinWeb, r.Owner));
        Assert.DoesNotContain(combos, c => c.Contains("Ctrl+", StringComparison.Ordinal) || c.Contains("Alt+", StringComparison.Ordinal) || c.Contains("Meta+", StringComparison.Ordinal));
    }

    [Fact]
    public void WithJeInstalled_ItsEffectiveKeys_TheDigits_AndThePanelKey_AreReserved()
    {
        var snapshot = Snapshot(new JePluginState(true, true, "12.11.0.0", "Active"), Fixture);

        var reservations = ShortcutPolicy.JellyfinEnhanced(snapshot);

        var b = Assert.Single(reservations, r => r.Combo == "B");
        Assert.Equal(ShortcutOwner.JellyfinEnhanced, b.Owner);
        Assert.Equal("Jellyfin Enhanced's Bookmark Current Time", b.Description);
        Assert.False(b.IsDefault);
        Assert.Null(b.UserId);
        Assert.Contains(reservations, r => r.Combo == "Shift+H");
        Assert.Contains(reservations, r => r.Combo == "/" && r.Description == "Jellyfin Enhanced's Open Search");
        Assert.Contains(reservations, r => r.Combo == "7" && r.Description == "Jellyfin Enhanced's Jump to % of video");
        Assert.Contains(reservations, r => r.Combo == "Shift+7");
        Assert.Contains(reservations, r => r.Combo == "?" && r.Description == "Jellyfin Enhanced's help panel");
        Assert.Contains(reservations, r => r.Combo == "Shift+?");
        Assert.DoesNotContain(reservations, r => r.Combo == "0-9");
        Assert.DoesNotContain(reservations, r => r.Combo == "Shift+B");
    }

    [Fact]
    public void AClearedOrUnpressableKey_ReservesNothing_AndDisableAllShortcuts_LeavesOnlyThePanel()
    {
        var cleared = new JeSettings(true, new Dictionary<string, bool>(StringComparer.Ordinal), [new("BookmarkCurrentTime", string.Empty), new("SubtitleMenu", "Disabled"), new("JumpToPercentage", string.Empty)], null);
        var reservations = ShortcutPolicy.JellyfinEnhanced(Snapshot(new JePluginState(true, true, null, "Active"), cleared));
        Assert.DoesNotContain(reservations, r => r.Combo == "B");
        Assert.DoesNotContain(reservations, r => r.Combo == "S");
        Assert.DoesNotContain(reservations, r => r.Combo == "5");
        Assert.Contains(reservations, r => r.Combo == "/");

        var all = new JeSettings(true, new Dictionary<string, bool>(StringComparer.Ordinal) { ["DisableAllShortcuts"] = true }, Fixture.Shortcuts, null);
        var onlyPanel = ShortcutPolicy.JellyfinEnhanced(Snapshot(new JePluginState(true, true, null, "Active"), all));
        Assert.Equal(["?", "Shift+?"], onlyPanel.Select(r => r.Combo));
    }

    [Fact]
    public void AUsersOwnKey_IsReservedForThatUser_WhileTheirClearedOne_IsNot()
    {
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        var snapshot = Snapshot(new JePluginState(true, true, null, "Active"), Fixture, [
            new JeUserShortcuts(alice, [new("BookmarkCurrentTime", "Shift+Alt+B"), new("OpenSearch", string.Empty)], null),
            new JeUserShortcuts(bob, [new("BookmarkCurrentTime", string.Empty)], null)
        ]);

        var reservations = ShortcutPolicy.JellyfinEnhanced(snapshot);

        var alices = Assert.Single(reservations, r => r.Combo == "Shift+Alt+B");
        Assert.Equal(alice, alices.UserId);
        Assert.Equal("Jellyfin Enhanced's Bookmark Current Time, for one user", alices.Description);
        Assert.Single(reservations, r => r.Combo == "B");
        Assert.Single(reservations, r => r.Combo == "/");
    }

    [Fact]
    public void Je1212sReading_IsReservedToo_SoAKeyTakenOnEitherVersionIsWarnedAbout()
    {
        // JE 12.12 reads a one-letter key in either case and the modifiers in any order, and takes a one-character symbol
        // pressed with or without Shift (ShortcutCombo.FromJellyfinEnhanced1212); the policy reserves what 12.11 and 12.12
        // act on, since the server runs one until a restart and the other after it.
        var settings = new JeSettings(true, new Dictionary<string, bool>(StringComparer.Ordinal), [new("BookmarkCurrentTime", "b"), new("SubtitleMenu", "Ctrl+Shift+S")], null);
        var snapshot = Snapshot(new JePluginState(true, true, "12.12.0.0", "Active"), settings);

        var reservations = ShortcutPolicy.JellyfinEnhanced(snapshot);

        var b = Assert.Single(reservations, r => r.Combo == "B");
        Assert.Equal("Jellyfin Enhanced's Bookmark Current Time", b.Description);
        Assert.Contains(reservations, r => r.Combo == "Shift+Ctrl+S" && r.Description == "Jellyfin Enhanced's Subtitle Menu");
        Assert.Single(reservations, r => r.Combo == "+");
        Assert.Contains(reservations, r => r.Combo == "Shift++" && r.Description == "Jellyfin Enhanced's Increase Playback Speed");
        Assert.Contains(reservations, r => r.Combo == "Shift+/" && r.Description == "Jellyfin Enhanced's Open Search");
        Assert.Contains(reservations, r => r.Combo == "Shift+," && r.Description == "Jellyfin Enhanced's Step Back One Frame");
        Assert.DoesNotContain(reservations, r => r.Combo == "Shift+B");
        Assert.DoesNotContain(reservations, r => r.Combo == "S");

        var check = Policy(settings).Check(new("BookmarkNow", "Bookmark this moment", "B"), snapshot);
        Assert.True(check.IsReserved);
        Assert.Equal("B is taken by Jellyfin Enhanced's Bookmark Current Time, so Bookmark this moment won't work with it. Choose another key.", check.Warning);
        Assert.False(Policy(settings).Check(new("BookmarkNow", "Bookmark this moment", "Shift+B"), snapshot).IsReserved);
    }

    [Fact]
    public void WithoutJe_ItsDefaults_AreReservedAsDefaults()
    {
        var reservations = ShortcutPolicy.JellyfinEnhanced(JellyfinEnhancedSnapshot.None);

        var b = Assert.Single(reservations, r => r.Combo == "B");
        Assert.True(b.IsDefault);
        Assert.Equal("Jellyfin Enhanced's Bookmark Current Time", b.Description);
        Assert.Contains(reservations, r => r.Combo == "Z" && r.IsDefault);
        Assert.DoesNotContain(reservations, r => r.UserId is not null);
    }

    [Fact]
    public void Check_NormalisesTheSetting_AndSaysWhoTakesIt()
    {
        var policy = Policy(Fixture);
        var snapshot = Snapshot(new JePluginState(true, true, null, "Active"), Fixture);

        var free = policy.Check(new("BookmarkNow", "Bookmark this moment", " shift+b "), snapshot);
        Assert.True(free.IsValid);
        Assert.Equal("Shift+B", free.Combo);
        Assert.False(free.IsReserved);
        Assert.Null(free.Warning);

        var taken = policy.Check(new("BookmarkNow", "Bookmark this moment", "b"), snapshot);
        Assert.Equal("B", taken.Combo);
        Assert.True(taken.IsReserved);
        Assert.Equal("B is taken by Jellyfin Enhanced's Bookmark Current Time, so Bookmark this moment won't work with it. Choose another key.", taken.Warning);

        var web = policy.Check(new("BookmarkNow", "Bookmark this moment", "k"), snapshot);
        Assert.Equal("K is taken by jellyfin-web's video player, so Bookmark this moment won't work with it. Choose another key.", web.Warning);

        var both = policy.Check(new("BookmarkNow", "Bookmark this moment", "/"), Snapshot(new JePluginState(true, true, null, "Active"), new JeSettings(true, new Dictionary<string, bool>(StringComparer.Ordinal), [new("OpenSearch", "/"), new("GoToHome", "/")], null)));
        Assert.Equal("/ is taken by Jellyfin Enhanced's Open Search and Jellyfin Enhanced's Go to Home, so Bookmark this moment won't work with it. Choose another key.", both.Warning);

        var off = policy.Check(new("BookmarkNow", "Bookmark this moment", "  "), snapshot);
        Assert.True(off.IsValid);
        Assert.Equal(string.Empty, off.Combo);
        Assert.Null(off.Warning);

        var invalid = policy.Check(new("BookmarkNow", "Bookmark this moment", "Shift+"), snapshot);
        Assert.False(invalid.IsValid);
        Assert.Null(invalid.Combo);
        Assert.Equal("\"Shift+\" isn't a key combination; use a key with optional modifiers, such as Shift+B.", invalid.Warning);

        var later = policy.Check(new("BookmarkNow", "Bookmark this moment", "B"), JellyfinEnhancedSnapshot.None);
        Assert.Equal("B is Jellyfin Enhanced's Bookmark Current Time by default, so Bookmark this moment would stop working if Jellyfin Enhanced is installed.", later.Warning);
    }

    [Fact]
    public void ReservedCombos_ListEachOnce_WebFirst()
    {
        var snapshot = Snapshot(new JePluginState(true, true, null, "Active"), Fixture);

        var combos = Policy(Fixture).AllReservedCombos(snapshot);

        Assert.Equal(combos.Count, combos.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("K", combos[0]);
        Assert.Contains("B", combos);
        Assert.Contains("Shift+H", combos);
        Assert.Contains("?", combos);
        Assert.Equal(combos, Policy(Fixture).ReservedCombos(Guid.NewGuid(), snapshot));
    }

    [Fact]
    public void ReservedCombos_ForAUser_HoldTheirOwnOverrides_AndNobodyElses()
    {
        // A capabilities response goes to any signed-in user, so it carries the server-wide keys and the caller's own
        // JE overrides only; the admin's Diagnostics lists everyone's.
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        var snapshot = Snapshot(new JePluginState(true, true, null, "Active"), Fixture, [
            new JeUserShortcuts(alice, [new("BookmarkCurrentTime", "Shift+Alt+B")], null),
            new JeUserShortcuts(bob, [new("OpenSearch", "Ctrl+K")], null)
        ]);
        var policy = Policy(Fixture);

        var alices = policy.ReservedCombos(alice, snapshot);
        Assert.Contains("Shift+Alt+B", alices);
        Assert.DoesNotContain("Ctrl+K", alices);
        Assert.Contains("B", alices);
        Assert.Contains("K", alices);

        var bobs = policy.ReservedCombos(bob, snapshot);
        Assert.Contains("Ctrl+K", bobs);
        Assert.DoesNotContain("Shift+Alt+B", bobs);

        var carols = policy.ReservedCombos(Guid.NewGuid(), snapshot);
        Assert.DoesNotContain("Shift+Alt+B", carols);
        Assert.DoesNotContain("Ctrl+K", carols);

        var all = policy.AllReservedCombos(snapshot);
        Assert.Contains("Shift+Alt+B", all);
        Assert.Contains("Ctrl+K", all);
    }

    [Fact]
    public void Reserved_ReadsTheSnapshot_WhenNoneIsGiven()
    {
        using var folder = new TempDirectory();
        File.WriteAllText(Path.Combine(folder.Path, "Jellyfin.Plugin.JellyfinEnhanced.xml"), "<PluginConfiguration><Shortcuts><Shortcut><Name>BookmarkCurrentTime</Name><Key>Shift+Alt+B</Key></Shortcut></Shortcuts></PluginConfiguration>");
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(p => p.PluginConfigurationsPath).Returns(folder.Path);
        var policy = new ShortcutPolicy(new JellyfinEnhancedReader(paths.Object, null, new ServiceCollection().BuildServiceProvider(), NullLogger.Instance));

        var combos = policy.ReservedCombos(Guid.NewGuid());

        Assert.Contains("Shift+Alt+B", combos);
        Assert.DoesNotContain("B", combos);
        Assert.Equal(combos, policy.AllReservedCombos());
    }

    private static ShortcutPolicy Policy(JeSettings settings)
    {
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(p => p.PluginConfigurationsPath).Returns(Path.GetTempPath());
        return new ShortcutPolicy(new JellyfinEnhancedReader(paths.Object, null, new ServiceCollection().BuildServiceProvider(), NullLogger.Instance));
    }

    private static JellyfinEnhancedSnapshot Snapshot(JePluginState plugin, JeSettings settings, IReadOnlyList<JeUserShortcuts>? users = null)
        => new(plugin, settings, users ?? [], new CustomTabsState(false, false, [], null), PluginPagesState.NotInstalled);
}
