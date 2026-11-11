using System;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.UserRatings.Domain;
using Jellyfin.Plugin.UserRatings.Import;
using Xunit;

namespace Jellyfin.Plugin.UserRatings.Tests.Domain;

/// <summary>
/// <see cref="ReviewText"/>: line endings, control characters, trimming, blank-line collapsing, the
/// length limits at 1999/2000/5000/20000, and an emoji surrogate pair at the boundary.
/// </summary>
public sealed class ReviewTextTests
{
    [Theory]
    [InlineData("a\r\nb\rc\nd", "a\nb\nc\nd")]
    [InlineData("  \t spaced \n ", "spaced")]
    [InlineData("tab\tkept", "tab\tkept")]
    [InlineData("nul\0removed", "nulremoved")]
    [InlineData("\0\0", null)]
    [InlineData("bell\u0007, del\u007F, c1\u0085 gone", "bell, del, c1 gone")]
    [InlineData("", null)]
    [InlineData(" \r\n\t ", null)]
    [InlineData(null, null)]
    public void Normalize_FixesLineEndings_RemovesControls_AndTrims(string? text, string? expected)
        => Assert.Equal(expected, ReviewText.Normalize(text));

    [Theory]
    [InlineData("​")]
    [InlineData(" ​‍⁠﻿ \n‌ ")]
    [InlineData("‮")]
    [InlineData("⁦⁩‎‏")]
    [InlineData("\U000E0041\U000E007F")]
    public void OnlyFormatCharacters_AreNoReview_ThePlainEmpty400(string text)
    {
        // A review of only U+200B used to be saved and counted in ReviewCount. Tag characters are Cf too,
        // outside the BMP (a surrogate pair each).
        Assert.Null(ReviewText.Normalize(text));
        Assert.Equal(ReviewText.EmptyMessage, Assert.Throws<PluginException>(() => ReviewText.Validate(text, 5000)).Message);
    }

    [Theory]
    [InlineData("a​b")]
    [InlineData("\U0001F469‍\U0001F4BB")]
    [InlineData("‮abc")]
    [InlineData("שלום ⁧abc⁩")]
    [InlineData("\U0001F3F4\U000E0067\U000E0062\U000E0065\U000E006E\U000E0067\U000E007F")]
    public void FormatCharactersInAReviewWithText_AreKeptAsWritten(string text)
    {
        // Joiners make emoji and letters; the bidi controls stay (an override without its end too): the page isolates each
        // review (dir="auto", unicode-bidi: isolate).
        Assert.Equal(text, ReviewText.Normalize(text));
        Assert.True(ReviewText.HasText(text));
    }

    [Theory]
    [InlineData("\u202EAlice\u202C", "Alice")]
    [InlineData("\u2067\u05E9\u05E8\u05D4\u2069 Cohen", "\u05E9\u05E8\u05D4 Cohen")]
    [InlineData("Bo\u200F\u200E\u202A\u202B\u202D\u2066\u2068 Lee", "Bo Lee")]
    [InlineData("\uFEFFFormer\u0000 member\tB", "Former member B")]
    [InlineData("\U0001F469\u200D\U0001F4BB Kim", "\U0001F469\u200D\U0001F4BB Kim")]
    [InlineData("\u0645\u200C\u06CC", "\u0645\u200C\u06CC")]
    [InlineData("\u202E\u2069\uFEFF", null)]
    public void AName_DropsTheBidiControlsAndTheBom_KeepsTheJoiners_AReviewKeepsThemAll(string name, string? expected)
    {
        // A name an import file brings is a label (lists, logs, reasons), where an
        // override only reorders what follows; a review keeps every format character as written (it is
        // how a member mixes right-to-left and left-to-right words), and the page isolates each one.
        Assert.Equal(expected, ImportRowRules.CleanName(name));
        Assert.Equal(expected is null ? string.Empty : expected, ImportRowRules.Shown(name));
        if (ReviewText.HasText(name))
        {
            Assert.Equal(name.Replace("\u0000", string.Empty, StringComparison.Ordinal), ReviewText.Normalize(name));
        }

        // An item's name is matched against the library's, which keeps its marks: only the controls go.
        Assert.Equal(name.Replace("\u0000", string.Empty, StringComparison.Ordinal).Replace('\t', ' ').Trim(), ImportRowRules.CleanTitle(name) ?? string.Empty);
    }

    [Fact]
    public void MoreThanThreeBlankLines_CollapseToTwo_ThreeStay()
    {
        Assert.Equal("a\n\n\nb", ReviewText.Normalize("a\n\n\n\n\n\n\nb"));
        Assert.Equal("a\n\n\nb", ReviewText.Normalize("a\n \n\t\n  \n\n\nb"));
        Assert.Equal("a\n\n\n\nb", ReviewText.Normalize("a\n\n\n\nb"));
        Assert.Equal("a\n\nb\n\n\nc", ReviewText.Normalize("a\n\nb\r\n\r\n\r\n\r\n\r\nc"));
    }

    [Fact]
    public void ALoneSurrogate_BecomesTheReplacementCharacter_APairStays()
    {
        Assert.Equal("x�y", ReviewText.Normalize("x\uD83Dy"));
        Assert.Equal("x�y", ReviewText.Normalize("x\uDE00y"));
        Assert.Equal("x😀y", ReviewText.Normalize("x😀y"));
        Assert.Equal("�", ReviewText.Normalize("\uD83D"));
    }

    [Theory]
    [InlineData(1999, 2000, true)]
    [InlineData(2000, 2000, true)]
    [InlineData(2001, 2000, false)]
    [InlineData(5000, 5000, true)]
    [InlineData(5001, 5000, false)]
    [InlineData(20000, 20000, true)]
    [InlineData(20001, 20000, false)]
    public void Validate_HoldsTheLimit(int length, int limit, bool accepted)
    {
        var text = new string('x', length);
        if (accepted)
        {
            Assert.Equal(text, ReviewText.Validate(text, limit));
        }
        else
        {
            var refused = Assert.Throws<PluginException>(() => ReviewText.Validate(text, limit));
            Assert.Equal(PluginErrorKind.Invalid, refused.Kind);
            Assert.Contains("at most " + limit, refused.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AnEmojiAtTheBoundary_CountsAsTheTwoUnitsTheBrowserCounts()
    {
        // The web client's maxlength and counter count UTF-16 code units; a pair is never split, the text is refused whole.
        Assert.Equal(2000, ReviewText.Validate(new string('x', 1998) + "😀", 2000).Length);
        Assert.Throws<PluginException>(() => ReviewText.Validate(new string('x', 1999) + "😀", 2000));
    }

    [Fact]
    public void Validate_MeasuresTheNormalisedText()
    {
        // 2000 characters plus spaces and carriage returns that normalisation removes.
        Assert.Equal(2000, ReviewText.Validate("   " + new string('x', 1000) + "\r\n" + new string('y', 999) + "   ", 2000).Length);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\0")]
    [InlineData(null)]
    public void AnEmptyReview_IsThe400(string? text)
    {
        var refused = Assert.Throws<PluginException>(() => ReviewText.Validate(text, 5000));
        Assert.Equal((PluginErrorKind.Invalid, ReviewText.EmptyMessage), (refused.Kind, refused.Message));
    }
}
