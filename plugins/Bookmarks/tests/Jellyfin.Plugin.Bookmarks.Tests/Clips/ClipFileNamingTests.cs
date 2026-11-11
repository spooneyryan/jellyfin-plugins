using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.Bookmarks.Clips;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Bookmarks.Services;
using Jellyfin.Plugin.Bookmarks.Tests.TestSupport;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Clips;

public class ClipFileNamingTests
{
    private static readonly long _start = TimeSpan.Parse("01:02:03", System.Globalization.CultureInfo.InvariantCulture).Ticks;
    private static readonly long _end = TimeSpan.Parse("01:03:10", System.Globalization.CultureInfo.InvariantCulture).Ticks;

    [Fact]
    public void BuildFileName_WithoutNote_UsesItemAndRange()
    {
        var name = ClipFileNaming.BuildFileName("Heat (1995)", null, _start, _end, BookmarkClipMode.Accurate);

        Assert.Equal("Heat (1995) [01.02.03-01.03.10].mp4", name);
    }

    [Fact]
    public void BuildFileName_WithNote_IncludesNoteAndModeExtension()
    {
        var name = ClipFileNaming.BuildFileName("Heat (1995)", "  Bank job ", _start, _end, BookmarkClipMode.Fast);

        Assert.Equal("Heat (1995) - Bank job [01.02.03-01.03.10].mkv", name);
    }

    [Fact]
    public void BuildFileName_ReplacesCharactersInvalidOnWindows()
    {
        var name = ClipFileNaming.BuildFileName("Star Wars: Episode IV", "What?/Why* <now>|\"ok\"", 0, TimeSpan.TicksPerSecond, BookmarkClipMode.Accurate);

        Assert.Equal("Star Wars - Episode IV - What__Why_ _now___ok_ [00.00.00-00.00.01].mp4", name);
        Assert.Equal(-1, name.IndexOfAny(['<', '>', ':', '"', '/', '\\', '|', '?', '*']));
    }

    [Fact]
    public void BuildFileName_CollapsesWhitespaceAndControlCharacters()
    {
        var name = ClipFileNaming.BuildFileName("A\tB\r\n  C", null, 0, TimeSpan.TicksPerSecond, BookmarkClipMode.Accurate);

        Assert.StartsWith("A B C [", name, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildFileName_TruncatesLongNames()
    {
        var name = ClipFileNaming.BuildFileName(new string('x', 500), new string('y', 500), 0, TimeSpan.TicksPerSecond, BookmarkClipMode.Accurate);

        Assert.True(name.Length < 200, $"Name was {name.Length} characters long");
        Assert.EndsWith("[00.00.00-00.00.01].mp4", name, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildFileName_WithBlankItemName_FallsBackToClip()
    {
        var name = ClipFileNaming.BuildFileName("  ", null, 0, TimeSpan.TicksPerSecond, BookmarkClipMode.Accurate);

        Assert.Equal("Clip [00.00.00-00.00.01].mp4", name);
    }

    [Fact]
    public void BuildFileName_TrimsTrailingDots()
    {
        var name = ClipFileNaming.BuildFileName("Dr.", "Wait...", 0, TimeSpan.TicksPerSecond, BookmarkClipMode.Accurate);

        Assert.Equal("Dr - Wait [00.00.00-00.00.01].mp4", name);
    }

    [Theory]
    [InlineData(0, "00.00.00")]
    [InlineData(3723.9, "01.02.03")]
    [InlineData(90000, "25.00.00")]
    [InlineData(-5, "00.00.00")]
    public void FormatTimestamp_UsesHoursMinutesSeconds(double seconds, string expected)
    {
        Assert.Equal(expected, ClipFileNaming.FormatTimestamp(TimeSpan.FromSeconds(seconds).Ticks));
    }

    [Fact]
    public void MakeUnique_ReturnsPreferredPathWhenFree()
    {
        var path = ClipFileNaming.MakeUnique("clips", "a.mp4", _ => false);

        Assert.Equal(Path.Combine("clips", "a.mp4"), path);
    }

    [Fact]
    public void MakeUnique_AddsCounterWhenTaken()
    {
        var taken = new HashSet<string> { Path.Combine("clips", "a.mp4"), Path.Combine("clips", "a (2).mp4") };

        var path = ClipFileNaming.MakeUnique("clips", "a.mp4", taken.Contains);

        Assert.Equal(Path.Combine("clips", "a (3).mp4"), path);
    }

    [Theory]
    [InlineData("alice", "alice")]
    [InlineData("Bob: The Builder", "Bob - The Builder")]
    [InlineData("con", "con_")]
    [InlineData("LPT1", "LPT1_")]
    [InlineData("  ", "fallback")]
    [InlineData(null, "fallback")]
    public void BuildFolderName_ProducesSafeFolderNames(string? userName, string expected)
    {
        Assert.Equal(expected, ClipFileNaming.BuildFolderName(userName, "fallback"));
    }

    [Theory]
    [InlineData("Heat (1995)", "Vault", "Heat (1995) - Bank job [01.52.10-01.55.00].mp4", "Heat (1995) - Vault [01.52.10-01.55.00].mp4")]
    [InlineData("Heat (1995)", "Vault", "Heat (1995) - Bank job [01.52.10-01.55.00] (2).mkv", "Heat (1995) - Vault [01.52.10-01.55.00].mkv")]
    [InlineData("Heat (1995)", null, "Heat (1995) - Bank job [100.00.00-100.00.30].mp4", "Heat (1995) [100.00.00-100.00.30].mp4")]
    [InlineData("Heat: Director's Cut", "a/b", "Heat - Director's Cut - x [00.00.01-00.00.02].mp4", "Heat - Director's Cut - a_b [00.00.01-00.00.02].mp4")]
    [InlineData("", "CON", "Clip - x [00.00.01-00.00.02].mp4", "Clip - CON [00.00.01-00.00.02].mp4")]
    public void BuildRenamedFileName_KeepsTheRangeTheFilesNameSays_AndItsMode_WithTheStemOfNow(string itemName, string? label, string current, string expected)
    {
        // Clip rename: the file's own range (not the record's ticks, which a remake that failed may have moved), no " (2)".
        Assert.Equal(expected, ClipFileNaming.BuildRenamedFileName(itemName, label, current, 0, 1));
    }

    [Fact]
    public void BuildRenamedFileName_OfANameBookmarksDoesntGive_TakesTheFilesRangeFromTheRecord_OrNone()
    {
        Assert.Equal(
            "Heat (1995) - Vault [00.01.00-00.01.30].mp4",
            ClipFileNaming.BuildRenamedFileName("Heat (1995)", "Vault", "my clip.MP4", 60 * TimeSpan.TicksPerSecond, 90 * TimeSpan.TicksPerSecond));
        Assert.Null(ClipFileNaming.BuildRenamedFileName("Heat (1995)", "Vault", "my clip.mp4", null, 90 * TimeSpan.TicksPerSecond));

        // What it gives is a name the pattern takes (the janitor's and the scan's), as every new clip's name.
        Assert.True(ClipNamePattern.IsClipFileName(ClipFileNaming.BuildRenamedFileName("Heat (1995)", "Vault", "my clip.mkv", 0, 1)));
    }

    // Found in testing: the 80-character cut split an emoji's surrogate pair when it straddled the limit, leaving a lone
    // high surrogate in the file name, which ffmpeg and the JSON store write as U+FFFD: the record named a file that never
    // existed, and the next start deleted ffmpeg's output as a stale .partial (reproduced on Bookmarks 1.1.2).
    [Theory]
    [InlineData(78, 80)]
    [InlineData(79, 79)]
    [InlineData(80, 80)]
    [InlineData(81, 80)]
    public void Sanitize_NeverCutsAnEmojiInTwo_WhereverItMeetsTheLimit(int before, int expectedLength)
    {
        // "g" × before, then the trophy (two code units), then more text.
        var name = new string('g', before) + "\U0001F3C6" + "goal";

        var sanitized = ClipFileNaming.Sanitize(name, 80);

        Assert.Equal(expectedLength, sanitized.Length);
        Assert.True(sanitized.Length <= 80);
        Assert.False(HasLoneSurrogate(sanitized), sanitized);
        Assert.StartsWith(new string('g', Math.Min(before, 80)), sanitized, StringComparison.Ordinal);
        Assert.Equal(before <= 78, sanitized.Contains("\U0001F3C6", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildFileName_WithAnEmojiAtTheEightiethCharacter_GivesAFileNameWithoutHalfACharacter()
    {
        var label = new string('g', 79) + "\U0001F3C6" + "goal";

        var name = ClipFileNaming.BuildFileName("Heat (1995)", label, _start, _end, BookmarkClipMode.Fast);
        var renamed = ClipFileNaming.BuildRenamedFileName("Heat (1995)", label, "x [01.02.03-01.03.10].mkv", null, null)!;

        Assert.Equal("Heat (1995) - " + new string('g', 79) + " [01.02.03-01.03.10].mkv", name);
        Assert.Equal(name, renamed);
        Assert.False(HasLoneSurrogate(name));

        // The name survives the round trip through UTF-8 (the JSON store's, ffmpeg's arguments) unchanged.
        Assert.Equal(name, System.Text.Encoding.UTF8.GetString(System.Text.Encoding.UTF8.GetBytes(name)));
    }

    [Fact]
    public void BuildFileName_OfALongName_OrAVideoNameOverTheLimit_WithAnEmojiAtTheEightiethCharacter_HasNoHalfCharacter()
    {
        // Bookmarks 1.1.3: the same cut takes the label (in 1.1.3 a note when the bookmark had no name; since 1.2.0 the name
        // alone, BookmarkNames.ClipFileLabel), and the video's own name when it is over 80 characters.
        var text = new string('g', 79) + "\U0001F3C6" + "goal";
        var noted = TestData.CreateBookmark(Guid.NewGuid()) with { Name = text, Note = "a note" };

        var fromNote = ClipFileNaming.BuildFileName("Heat (1995)", BookmarkNames.ClipFileLabel(noted), _start, _end, BookmarkClipMode.Accurate);
        var fromVideo = ClipFileNaming.BuildFileName(text, null, _start, _end, BookmarkClipMode.Accurate);

        Assert.Equal("Heat (1995) - " + new string('g', 79) + " [01.02.03-01.03.10].mp4", fromNote);
        Assert.Equal(new string('g', 79) + " [01.02.03-01.03.10].mp4", fromVideo);
        Assert.False(HasLoneSurrogate(fromNote));
        Assert.False(HasLoneSurrogate(fromVideo));
    }

    [Fact]
    public void Sanitize_DropsLoneSurrogates_AndKeepsWholePairs()
    {
        Assert.Equal("ab", ClipFileNaming.Sanitize("a\uD83Cb", 80));
        Assert.Equal("ab", ClipFileNaming.Sanitize("a\uDFC6b", 80));
        Assert.Equal("a\U0001F3C6b", ClipFileNaming.Sanitize("a\U0001F3C6b", 80));
        Assert.Equal("ab", ClipFileNaming.Sanitize("a\uDFC6\uD83Cb", 80));
        Assert.Equal(string.Empty, ClipFileNaming.Sanitize("\uD83C", 80));
    }

    [Fact]
    public void Sanitize_KeepsALetterWithItsCombiningMarks_AndAnEmojiSequenceWhole()
    {
        // "e" + COMBINING ACUTE ACCENT straddling the limit: both go, never the letter alone.
        var accented = ClipFileNaming.Sanitize(new string('a', 79) + "e\u0301" + "z", 80);
        Assert.Equal(new string('a', 79), accented);

        // Within the limit, kept as written (no normalisation: the name is the record's).
        Assert.Equal("Cafe\u0301", ClipFileNaming.Sanitize("Cafe\u0301", 80));

        // A family emoji (a ZWJ sequence of 8 code units) at the limit: all of it or none of it.
        var family = "\U0001F468\u200D\U0001F469\u200D\U0001F467";
        Assert.Equal(new string('f', 75), ClipFileNaming.Sanitize(new string('f', 75) + family, 80));
        Assert.Equal(new string('f', 72) + family, ClipFileNaming.Sanitize(new string('f', 72) + family + "!", 80));

        // A thumbs-up with a skin tone (two pairs) cut before its modifier would change the emoji: it goes whole.
        Assert.Equal(new string('t', 77), ClipFileNaming.Sanitize(new string('t', 77) + "\U0001F44D\U0001F3FD", 80));
    }

    [Fact]
    public void BuildFolderName_OfAUserNameWithAnEmojiAtTheLimit_HasNoHalfCharacter()
    {
        var folder = ClipFileNaming.BuildFolderName(new string('u', 79) + "\U0001F600", "fallback");

        Assert.Equal(new string('u', 79), folder);
    }

    private static bool HasLoneSurrogate(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(text[i]))
            {
                return true;
            }
        }

        return false;
    }
}
