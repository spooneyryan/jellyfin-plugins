using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.Kit.Interop;
using Jellyfin.Plugin.Kit.TestSupport.PluginIdentity;
using Xunit;

namespace Jellyfin.Plugin.Kit.Tests.Interop;

/// <summary>
/// <see cref="ShortcutCombo"/> against the cases of <c>kit/testdata/shortcuts/combos.json</c>, which
/// <c>kit/web/tests/shortcuts.test.mjs</c> runs on <c>kit.shortcuts</c> too, so both sides read a combination the same way.
/// </summary>
public sealed class ShortcutComboTests
{
    private static readonly string CasesFile = Path.Combine(PluginIdentityChecks.FindRepositoryRoot(), "kit", "testdata", "shortcuts", "combos.json");

    public static TheoryData<string, string?> NormalizeCases() => Cases("normalize", "in", "out");

    public static TheoryData<string, string?> JellyfinEnhancedCases() => Cases("fromJellyfinEnhanced", "in", "out");

    [Theory]
    [MemberData(nameof(NormalizeCases))]
    public void Normalize_FollowsTheSharedCases(string text, string? expected)
    {
        Assert.Equal(expected, ShortcutCombo.Normalize(text));
        Assert.Equal(expected is not null, ShortcutCombo.TryNormalize(text, out var combo));
        Assert.Equal(expected ?? string.Empty, combo);
    }

    [Theory]
    [MemberData(nameof(JellyfinEnhancedCases))]
    public void FromJellyfinEnhanced_FollowsTheSharedCases(string key, string? expected)
    {
        Assert.Equal(expected, ShortcutCombo.FromJellyfinEnhanced(key));
    }

    public static TheoryData<string, string[]> JellyfinEnhanced1212Cases()
    {
        var data = new TheoryData<string, string[]>();
        using var document = JsonDocument.Parse(File.ReadAllText(CasesFile));
        foreach (var c in document.RootElement.GetProperty("fromJellyfinEnhanced1212").EnumerateArray())
        {
            data.Add(c.GetProperty("in").GetString()!, [.. c.GetProperty("out").EnumerateArray().Select(e => e.GetString()!)]);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(JellyfinEnhanced1212Cases))]
    public void FromJellyfinEnhanced1212_FollowsTheSharedCases(string key, string[] expected)
    {
        // JE 12.12: events.js canonicalCombo after config.js normalizeShortcutKey, a key press through config.js keyCombo.
        Assert.Equal(expected, ShortcutCombo.FromJellyfinEnhanced1212(key));
    }

    [Theory]
    [InlineData("B", new[] { "B" })]
    [InlineData("b", new[] { "B" })]
    [InlineData("Shift+B", new[] { "Shift+B" })]
    [InlineData("Ctrl+Shift+B", new[] { "Shift+Ctrl+B" })]
    [InlineData("+", new[] { "+", "Shift++" })]
    [InlineData("Shift++", new[] { "Shift++", "+" })]
    [InlineData("Shift+/", new[] { "Shift+/" })]
    [InlineData("Disabled", new[] { "Disabled" })]
    [InlineData("", new string[0])]
    public void JellyfinEnhancedCombos_AreBothReadings_The1211OneFirst_EachOnce(string key, string[] expected)
    {
        // What the policy and the checklist take, so they hold on 12.11 until the restart and on 12.12 after it.
        Assert.Equal(expected, ShortcutCombo.JellyfinEnhancedCombos(key));
        Assert.Empty(ShortcutCombo.JellyfinEnhancedCombos(null));
    }

    [Fact]
    public void IsPressable_FollowsTheSharedCases()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(CasesFile));
        foreach (var c in document.RootElement.GetProperty("pressable").EnumerateArray())
        {
            var combo = c.GetProperty("combo").GetString();
            Assert.True(c.GetProperty("out").GetBoolean() == ShortcutCombo.IsPressable(combo), combo);
        }
    }

    [Fact]
    public void Null_AndWhiteSpace_MeanNoShortcut()
    {
        Assert.Equal(string.Empty, ShortcutCombo.Normalize(null));
        Assert.Equal(string.Empty, ShortcutCombo.Normalize(" \t "));
        Assert.Null(ShortcutCombo.FromJellyfinEnhanced(null));
        Assert.Null(ShortcutCombo.KeyOf(null));
        Assert.Equal("K", ShortcutCombo.KeyOf("Shift+Ctrl+K"));
        Assert.Equal("+", ShortcutCombo.KeyOf("Shift++"));
    }

    [Fact]
    public void TheModifierOrder_IsJellyfinEnhanceds()
    {
        // JE builds Shift+, Meta+, Ctrl+, Alt+ in that order (events.js), whatever order the admin typed.
        Assert.Equal(["Shift", "Meta", "Ctrl", "Alt"], ShortcutCombo.Modifiers);
        Assert.Equal("Shift+Meta+Ctrl+Alt+B", ShortcutCombo.Normalize("alt+ctrl+meta+shift+b"));
    }

    private static TheoryData<string, string?> Cases(string section, string input, string output)
    {
        var data = new TheoryData<string, string?>();
        using var document = JsonDocument.Parse(File.ReadAllText(CasesFile));
        foreach (var c in document.RootElement.GetProperty(section).EnumerateArray())
        {
            var expected = c.GetProperty(output);
            data.Add(c.GetProperty(input).GetString()!, expected.ValueKind == JsonValueKind.Null ? null : expected.GetString());
        }

        return data;
    }
}
