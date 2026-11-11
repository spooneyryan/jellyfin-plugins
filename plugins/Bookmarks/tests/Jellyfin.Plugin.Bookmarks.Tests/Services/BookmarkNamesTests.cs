using System;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Bookmarks.Services;
using Jellyfin.Plugin.Bookmarks.Tests.TestSupport;
using Jellyfin.Plugin.Kit.Api;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Services;

/// <summary>
/// The name rules (which the 1→2 upgrade applies to a note that becomes a name, and create, update and rename apply
/// to every name since 1.1.0) and the clip label: null, white space, 100 vs 101 characters, CR/LF/TAB,
/// control characters (NEL and vertical tab among them), NFC (a combining accent, and the singletons Å and Ω), emoji.
/// </summary>
public sealed class BookmarkNamesTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   \t\r\n ", null)]
    [InlineData("Bank job", "Bank job")]
    [InlineData("  Bank job  ", "Bank job")]
    [InlineData("Bank\r\njob", "Bank job")]
    [InlineData("Bank\tjob", "Bank job")]
    [InlineData("Bank     job", "Bank job")]
    [InlineData("Bank\u0000job\u0007", "Bankjob")]
    [InlineData("\u0001\u0002", null)]
    [InlineData("\u212b\u2126", "\u00c5\u03a9")]
    [InlineData("Bank\u0085job\u000bnow\u007f", "Bankjobnow")]
    [InlineData("Café", "Café")]
    [InlineData("🎬 Bank job 🏦", "🎬 Bank job 🏦")]
    [InlineData("Amélie, café scène ✂ 日本語", "Amélie, café scène ✂ 日本語")]
    public void NormalizeName_FollowsTheRules(string? given, string? expected)
    {
        Assert.Equal(expected, BookmarkNames.NormalizeName(given));
    }

    [Fact]
    public void NormalizeName_100CharactersAreFine_101AreNot()
    {
        Assert.Equal(new string('x', 100), BookmarkNames.NormalizeName(new string('x', 100)));
        Assert.Equal(new string('x', 100), BookmarkNames.NormalizeName("  " + new string('x', 100) + "\t"));

        var ex = Assert.Throws<BookmarkException>(() => BookmarkNames.NormalizeName(new string('x', 101)));
        Assert.Equal(PluginErrorKind.Invalid, ex.Kind);
        Assert.Equal("Names can be at most 100 characters long.", ex.Message);
    }

    [Fact]
    public void NormalizeName_CountsUtf16Units_AfterComposition()
    {
        // 50 emoji are 100 UTF-16 units; 50 "e" + combining accents compose to 50 characters.
        Assert.NotNull(BookmarkNames.NormalizeName(string.Concat(System.Linq.Enumerable.Repeat("🎬", 50))));
        Assert.Throws<BookmarkException>(() => BookmarkNames.NormalizeName(string.Concat(System.Linq.Enumerable.Repeat("🎬", 51))));
        Assert.Equal(new string('é', 60), BookmarkNames.NormalizeName(string.Concat(System.Linq.Enumerable.Repeat("é", 60))));
    }

    [Fact]
    public void NormalizeName_AnUnpairedSurrogate_IsInvalid()
    {
        Assert.False(BookmarkNames.TryNormalizeName("Bank \ud800 job", out var name, out var error));
        Assert.Null(name);
        Assert.Equal(BookmarkNames.InvalidTextMessage, error);
        Assert.Equal(PluginErrorKind.Invalid, Assert.Throws<BookmarkException>(() => BookmarkNames.NormalizeName("\udc00")).Kind);
    }

    [Theory]
    [InlineData("Vault", "Bank job", "Vault")]
    [InlineData(null, "Bank job", "Bank job")]
    [InlineData(" ", "Bank job", "Bank job")]
    [InlineData(null, " ", null)]
    [InlineData(null, null, null)]
    public void Label_IsTheName_ElseTheNote(string? name, string? note, string? label)
    {
        Assert.Equal(label, BookmarkNames.Label(TestData.CreateBookmark(Guid.NewGuid()) with { Name = name, Note = note }));
    }

    [Fact]
    public void FromLabel_IsTheNormalisedName_ALongLabelCutWithTheWholeInTheNote_NeverRefused()
    {
        // JE's label becomes the name; blank gives none; over 100 characters the name is the first 100
        // and the note keeps the whole label (cut at the note's limit); text a name would refuse is repaired, not refused.
        Assert.Equal((null, null), BookmarkNames.FromLabel(null, 500));
        Assert.Equal((null, null), BookmarkNames.FromLabel(" \t ", 500));
        Assert.Equal(("Line one line two", null), BookmarkNames.FromLabel("Line one\nline\ttwo", 500));
        Assert.Equal(("Bad � text", null), BookmarkNames.FromLabel("Bad \uD800 text", 500));

        var long101 = new string('a', 95) + " bcdef";
        var (name, note) = BookmarkNames.FromLabel(long101, 500);
        Assert.Equal(new string('a', 95) + " bcde", name);
        Assert.Equal(long101, note);

        var (cutName, cutNote) = BookmarkNames.FromLabel(new string('x', 99) + "🎬 tail", 500);
        Assert.Equal(new string('x', 99), cutName);
        Assert.Equal(new string('x', 99) + "🎬 tail", cutNote);

        var (_, longNote) = BookmarkNames.FromLabel(new string('n', 600), 500);
        Assert.Equal(500, longNote!.Length);
    }
}
