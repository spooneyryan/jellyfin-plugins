using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.Bookmarks.Clips;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Bookmarks.Tests.TestSupport;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Clips;

/// <summary>
/// The clip name pattern: every name <see cref="ClipFileNaming.BuildFileName"/> and
/// <see cref="ClipFileNaming.MakeUnique"/> give matches it, with and without <c>.partial</c>, and names they never give
/// (another program's files, a finished movie in a library folder, a hand-renamed clip) don't, so the janitor never deletes
/// them.
/// </summary>
public sealed class ClipNamePatternTests
{
    public static TheoryData<string, string?, long, long, BookmarkClipMode> Names => new()
    {
        { "Heat (1995)", null, 0, TimeSpan.TicksPerSecond, BookmarkClipMode.Accurate },
        { "Heat (1995)", "Bank job", 62 * TimeSpan.TicksPerMinute, 63 * TimeSpan.TicksPerMinute, BookmarkClipMode.Fast },
        { "Star Wars: Episode IV", "What?/Why* <now>|\"ok\"", 0, TimeSpan.TicksPerSecond, BookmarkClipMode.Accurate },
        { "   ", null, 5, 10, BookmarkClipMode.Fast },
        { "Amélie", "café scène ✂ 日本語 [a-b] (2)", TimeSpan.TicksPerHour, TimeSpan.TicksPerHour + 1, BookmarkClipMode.Accurate },
        { new string('x', 200), new string('y', 200), 0, 1, BookmarkClipMode.Fast },
        { "Long", "over 99 hours", 120 * TimeSpan.TicksPerHour, 121 * TimeSpan.TicksPerHour, BookmarkClipMode.Accurate },
        { "CON", "NUL", 0, 1, BookmarkClipMode.Fast },
        { "Dots...", "Trailing ", 0, 1, BookmarkClipMode.Accurate }
    };

    [Theory]
    [MemberData(nameof(Names))]
    public void EveryNameBookmarksGives_Matches_WithAndWithoutPartial(string itemName, string? label, long start, long end, BookmarkClipMode mode)
    {
        var name = ClipFileNaming.BuildFileName(itemName, label, start, end, mode);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string> { name };
        for (var i = 0; i < 3; i++)
        {
            var unique = ClipFileNaming.MakeUnique("C:\\clips", name, taken.Contains);
            taken.Add(unique);
            names.Add(Path.GetFileName(unique));
        }

        // " (2)", " (3)"... and the fallback after 999 names.
        names.Add(Path.GetFileName(ClipFileNaming.MakeUnique("C:\\clips", name, _ => true)));
        Assert.Matches(@" \([0-9a-f]{32}\)\.(mp4|mkv)\z", names[^1]);

        Assert.All(names, n =>
        {
            Assert.True(ClipNamePattern.IsClipFileName(n), n);
            Assert.False(ClipNamePattern.IsPartialFileName(n), n);
            Assert.True(ClipNamePattern.IsPartialFileName(n + ClipFileGuard.PartialExtension), n + ".partial");
            Assert.False(ClipNamePattern.IsClipFileName(n + ClipFileGuard.PartialExtension), n + ".partial");
        });
    }

    [Theory]
    [InlineData("movie.mkv.partial")]                                            // no bracket pattern
    [InlineData("Heat (1995).mkv")]                                              // a film in a library folder
    [InlineData("Heat [00.01.00-00.01.30].avi.partial")]                         // another extension
    [InlineData("Heat [00.01.00-00.01.30].mkv.part")]
    [InlineData("Heat [00.01.00-00.01.30].mkv.partial.old")]
    [InlineData("Heat [00.01.00-00.01.30].partial")]
    [InlineData("Heat [00.01.00-00.01.30].MKV.partial")]                         // Bookmarks writes the extensions in lower case
    [InlineData("Heat [00.01.00-00.01.30].mkv.PARTIAL")]
    [InlineData("Heat [0.01.00-00.01.30].mkv.partial")]                          // hours have two digits at least
    [InlineData("Heat [00.1.00-00.01.30].mkv.partial")]
    [InlineData("Heat [00.01.00-00.01.3].mkv.partial")]
    [InlineData("Heat [00:01:00-00:01:30].mkv.partial")]
    [InlineData("Heat [00.01.00 - 00.01.30].mkv.partial")]
    [InlineData("Heat[00.01.00-00.01.30].mkv.partial")]                          // no space before the range
    [InlineData(" [00.01.00-00.01.30].mkv.partial")]                             // no stem
    [InlineData("[00.01.00-00.01.30].mkv.partial")]
    [InlineData("Heat [00.01.00-00.01.30](2).mkv.partial")]
    [InlineData("Heat [00.01.00-00.01.30] (two).mkv.partial")]
    [InlineData("Heat [00.01.00-00.01.30] ().mkv.partial")]
    [InlineData("Heat [00.01.00-00.01.30] (0123456789ABCDEF0123456789ABCDEF).mkv.partial")]  // the fallback is lower case
    [InlineData("Heat [00.01.00-00.01.30] (0123456789abcdef0123456789abcde).mkv.partial")]   // 31 digits
    [InlineData("Heat [00.01.00-00.01.30] copy.mkv.partial")]
    [InlineData("Heat [٠٠.٠١.٠٠-٠٠.٠١.٣٠].mkv.partial")]                         // Arabic-Indic digits, which .NET's \d takes
    [InlineData("Heat [００.０１.００-００.０１.３０].mkv.partial")]                // fullwidth digits
    [InlineData("Heat [00.01.00-00.01.30].mkv.partial\n")]                       // $ would match before the line feed
    [InlineData("Heat [00.01.00-00.01.30].mkv\n.partial")]
    [InlineData("alice\\Heat [00.01.00-00.01.30].mkv.partial")]                 // a path, not a name
    [InlineData("alice/Heat [00.01.00-00.01.30].mkv.partial")]
    [InlineData("")]
    [InlineData(null)]
    public void NamesBookmarksNeverGives_DontMatch(string? name)
    {
        Assert.False(ClipNamePattern.IsPartialFileName(name), name);
        Assert.False(ClipNamePattern.IsClipFileName(name), name);
    }

    [Theory]
    [InlineData("Heat (1995) - Bank job [01.02.03-01.03.10].mkv.partial")]
    [InlineData("Heat (1995) [01.02.03-01.03.10] (2).mp4.partial")]
    [InlineData("Heat (1995) [01.02.03-01.03.10] (17).mkv.partial")]
    [InlineData("Heat (1995) [01.02.03-01.03.10] (0123456789abcdef0123456789abcdef).mkv.partial")]
    [InlineData("Heat [100.00.00-100.00.10].mp4.partial")]
    [InlineData("a [00.00.00-00.00.01].mkv.partial")]
    [InlineData("Heat [00.00.01-00.00.02] [00.00.01-00.00.02].mkv.partial")]  // a label with a range of its own
    public void PartialNamesOfClipsBeingWritten_Match(string name)
    {
        Assert.True(ClipNamePattern.IsPartialFileName(name));
        Assert.True(ClipNamePattern.IsClipFileName(name[..^ClipFileGuard.PartialExtension.Length]));
    }

    [Theory]
    [InlineData(StoreFixtures.Bookmarks100, 2)]
    [InlineData(StoreFixtures.Bookmarks10DevSandbox, 4)]
    public void TheClipFilesBookmarks10Wrote_AndTheirPartials_Match(string fixture, int clips)
    {
        // Real 1.0 data (1.0.0 may run on a server before 1.1.0): 1.0 named its clips, and wrote them as <name>.partial,
        // with the same BuildFileName and MakeUnique, so a 1.0 crash's leftover in a folder the 1→2 upgrade records is the
        // janitor's too.
        using var json = JsonDocument.Parse(StoreFixtures.Read(fixture));
        var names = json.RootElement.GetProperty("Bookmarks").EnumerateArray()
            .Select(b => b.TryGetProperty("Clip", out var clip) && clip.ValueKind == JsonValueKind.Object && clip.TryGetProperty("FilePath", out var file) ? file.GetString() : null)
            .OfType<string>()
            .Select(path => path[(path.LastIndexOfAny(['\\', '/']) + 1)..])
            .ToList();

        Assert.Equal(clips, names.Count);
        Assert.All(names, n => Assert.True(ClipNamePattern.IsClipFileName(n), n));
        Assert.All(names, n => Assert.True(ClipNamePattern.IsPartialFileName(n + ClipFileGuard.PartialExtension), n));
    }

    [Fact]
    public void ThePatternIsTheDocumentedOne_WithAsciiDigitsAndTheEndOfTheName()
    {
        // The documented expression with [0-9] for \d and \z for $: nothing else differs.
        const string Plan = @"^(?<stem>.+) \[(?<s>\d{2,}\.\d{2}\.\d{2})-(?<e>\d{2,}\.\d{2}\.\d{2})\](?: \((?:\d+|[0-9a-f]{32})\))?\.(?<ext>mp4|mkv)(?<partial>\.partial)?$";
        var ours = ClipNamePattern.Expression.Replace("(?<start>", "(?<s>", StringComparison.Ordinal).Replace("(?<end>", "(?<e>", StringComparison.Ordinal);

        Assert.Equal(Plan.Replace(@"\d", "[0-9]", StringComparison.Ordinal)[..^1] + @"\z", ours);
        Assert.DoesNotContain(@"\d", ClipNamePattern.Expression, StringComparison.Ordinal);
        Assert.EndsWith(@"\z", ClipNamePattern.Expression, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Heat (1995) - Bank job [01.52.10-01.55.00].mp4", "01.52.10", "01.55.00")]
    [InlineData("Heat (1995) - A [b] [100.00.00-100.00.30] (2).mkv", "100.00.00", "100.00.30")]
    [InlineData("Heat (1995) [00.00.01-00.00.02] (0123456789abcdef0123456789abcdef).mp4", "00.00.01", "00.00.02")]
    public void Range_IsTheRangeAFinishedClipsNameSays(string name, string start, string end)
    {
        Assert.Equal((start, end), ClipNamePattern.Range(name));
    }

    [Theory]
    [InlineData("Heat (1995) - Bank job [01.52.10-01.55.00].mp4.partial")]
    [InlineData("movie.mkv")]
    [InlineData(@"folder\Heat [00.00.01-00.00.02].mp4")]
    [InlineData(null)]
    public void Range_OfANameThatIsntAFinishedClips_IsNone(string? name)
    {
        Assert.Null(ClipNamePattern.Range(name));
    }
}
