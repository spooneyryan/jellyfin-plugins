using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.PluginIdentity;
using Xunit;

namespace Jellyfin.Plugins.Repo.Tests;

/// <summary>
/// Nothing personal in the repository's files: the roots PublicContentRules.json lists, walked on disk rather than asked
/// of git, so the test runs the same in any checkout. The rules and why each one is there are in PublicContentRules.json.
/// The test also applies the rules of an optional local rules file, <c>private/PublicContentRules.private.json</c>, when
/// present: <c>private/</c> is an optional local folder of extra rules and test data, gitignored and not part of this
/// repository. A hit names its file and line (line 0 is the file's path).
/// </summary>
public sealed class PublicContentGuardTests
{
    private static readonly string Root = PluginIdentityChecks.FindRepositoryRoot();

    private static readonly PublicContentRules Rules = PublicContentRules.Load(Path.Combine(Root, PublicContentRules.RelativePath));

    private static readonly string LocalRulesPath = Path.Combine(Root, PublicContentRules.LocalRelativePath);

    // The rules, and the local ones when present.
    private static readonly PublicContentRules AllRules = File.Exists(LocalRulesPath) ? Rules.With(PublicContentRules.Load(LocalRulesPath)) : Rules;

    // The roots, pinned so that a root added to the rules file is a decision, not a slip.
    private static readonly string[] Whitelist =
    [
        ".editorconfig", ".gitattributes", ".gitignore", "Directory.Build.props", "Directory.Build.targets",
        "Directory.Packages.props", "LICENSE", "README.md", "DEVELOPMENT.md", "jellyfin-plugins.slnx", "nuget.config",
        "manifest.json", ".github", "branding", "kit", "plugins", "scripts", "tests",
    ];

    [Fact]
    public void PublishedFiles_HoldNothingPersonal()
    {
        var files = PublicContentScanner.PublishedFiles(Root, Rules);
        Assert.True(files.Count > 100, $"the walk found only {files.Count} files under {Root}");
        Assert.Contains(PublicContentRules.RelativePath, files);
        Assert.Contains("tests/Repo.Tests/PublicContentGuardTests.cs", files);

        var hits = files.SelectMany(f => PublicContentScanner.Scan(f, File.ReadAllText(Path.Combine(Root, f)), AllRules)).ToList();

        Assert.True(
            hits.Count == 0,
            $"{hits.Count} hit(s) in the repository's files ({AllRules.Rules.Count} rules: {PublicContentRules.RelativePath}"
                + (File.Exists(LocalRulesPath) ? $" and {PublicContentRules.LocalRelativePath}" : string.Empty) + "):\n"
                + string.Join('\n', hits.Take(300)));
    }

    [Fact]
    public void Roots_AreTheWhitelist()
    {
        Assert.Equal(Whitelist, Rules.Roots);
    }

    [Fact]
    public void Walk_ReadsOnlyTheRoots_AndSkipsBuildOutputAndSandboxes()
    {
        using var tree = new TempDirectory();
        tree.CreateFile("LICENSE");
        tree.CreateFile("manifest.json");
        tree.CreateFile(Path.Combine(".github", "workflows", "ci.yml"));
        tree.CreateFile(Path.Combine(".git", "config"));
        tree.CreateFile(Path.Combine("kit", "src", "A.cs"));
        tree.CreateFile(Path.Combine("plugins", "P", "bin", "Release", "x.json"));
        tree.CreateFile(Path.Combine("plugins", "P", "obj", "y.cs"));
        tree.CreateFile(Path.Combine("plugins", "P", ".sandbox", "z.txt"));
        tree.CreateFile(Path.Combine("scripts", "artifacts", "w.txt"));
        tree.CreateFile(Path.Combine("tests", "T", "node_modules", "m.js"));
        tree.CreateFile(Path.Combine("tests", "T", "B.cs"));
        tree.CreateFile(Path.Combine("notes", "plan.md"));
        tree.CreateFile(Path.Combine("drafts", "S.cs"));
        tree.CreateFile(Path.Combine("local", "notes.md"));
        tree.CreateFile("binary.bin");
        File.WriteAllBytes(tree.Combine("kit", "data.bin"), [0x41, 0x00, 0x42]);

        var files = PublicContentScanner.PublishedFiles(tree.Path, Rules);

        Assert.Equal(new[] { ".github/workflows/ci.yml", "LICENSE", "kit/src/A.cs", "manifest.json", "tests/T/B.cs" }, files.OrderBy(f => f, StringComparer.Ordinal).ToArray());
    }

    // What looks like a profile path or an e-mail address but names no one.
    [Theory]
    [InlineData("C:\\Users\\..\\ProgramData\\Jellyfin\\Server and C:\\Users\\Public\\Videos")]
    [InlineData("C:\\Users\\Default and C:\\Users\\%USERNAME%\\source")]
    [InlineData("release-tests@example.invalid, http://user:pass@m3u.example/list.m3u, a@b.test")]
    [InlineData("noreply@anthropic.com, someone@example.org")]
    public void LeaveAloneCases_AreNotCaught(string text)
    {
        Assert.Empty(PublicContentScanner.Scan("kit/x.cs", text, Rules));
    }

    // Each written in two pieces so that this file holds none of them whole.
    [Theory]
    [InlineData("C:\\Users\\", "alice\\source", "windows-user-path")]
    [InlineData("C:\\\\Users\\\\", "alice\\\\source", "windows-user-path")]
    [InlineData("d:/Users/", "alice/source", "windows-user-path")]
    [InlineData("/c/Users/", "alice/source", "windows-user-path")]
    [InlineData("alice", "@corp.com", "email")]
    [InlineData("first.last+tag", "@mail.contoso.net", "email")]
    public void PersonalDetails_AreCaught(string head, string tail, string rule)
    {
        var hit = Assert.Single(PublicContentScanner.Scan("kit/x.cs", "first line\nthe value " + head + tail + " here\n", Rules));

        Assert.Equal(rule, hit.Rule);
        Assert.Equal(2, hit.Line);
    }

    [Fact]
    public void AllowAt_HoldsInItsOwnFileOnly()
    {
        using var tree = new TempDirectory();
        var file = tree.CreateFile("rules.json", """
            { "rules": [ { "id": "stand-in", "why": "A stand-in name.", "pattern": "(?i)orchard[0-9]*",
              "allowAt": [ { "path": "scripts/fruit-probe.mjs", "value": "orchard7", "why": "A stand-in exception." } ] } ] }
            """);
        var rules = PublicContentRules.Load(file);

        Assert.Empty(PublicContentScanner.Scan("scripts/fruit-probe.mjs", "/orchard7|Probe/i", rules));
        Assert.Equal("stand-in", Assert.Single(PublicContentScanner.Scan("scripts/fruit-probe.mjs", "/orchard8|Probe/i", rules)).Rule);
        Assert.Equal("stand-in", Assert.Single(PublicContentScanner.Scan("scripts/other-probe.mjs", "/orchard7|Probe/i", rules)).Rule);
    }

    [Fact]
    public void Paths_AreScannedToo()
    {
        var hit = Assert.Single(PublicContentScanner.Scan("plugins/P/notes-alice" + "@corp.com.md", "nothing here\n", Rules));

        Assert.Equal("email", hit.Rule);
        Assert.Equal(0, hit.Line);
    }

    [Fact]
    public void MoreRules_AreAddedToTheOnesHere()
    {
        using var tree = new TempDirectory();
        var file = tree.CreateFile("more.json", """
            { "rules": [ { "id": "stand-in", "why": "A stand-in name.", "pattern": "(?i)orchard" } ] }
            """);

        var all = Rules.With(PublicContentRules.Load(file));

        Assert.Equal(Rules.Rules.Count + 1, all.Rules.Count);
        Assert.Equal(Rules.Roots, all.Roots);
        Assert.Empty(PublicContentScanner.Scan("kit/a.md", "The Orchard was empty.", Rules));
        Assert.Equal("stand-in", Assert.Single(PublicContentScanner.Scan("kit/a.md", "The Orchard was empty.", all)).Rule);
    }

    [Fact]
    public void LocalRules_WhenPresent_SayWhy_CatchTheirExamples_AndLeaveTheirLookAlikesAlone()
    {
        Assert.SkipUnless(File.Exists(LocalRulesPath), $"No {PublicContentRules.LocalRelativePath} here.");

        var local = PublicContentRules.Load(LocalRulesPath);

        Assert.NotEmpty(local.Rules);
        Assert.All(local.Rules, r => Assert.False(string.IsNullOrWhiteSpace(r.Why), $"rule {r.Id} says why it is there"));
        Assert.Empty(local.Rules.Select(r => r.Id).Intersect(Rules.Rules.Select(r => r.Id)));
        foreach (var rule in local.Rules)
        {
            foreach (var example in rule.Examples)
            {
                var hits = PublicContentScanner.Scan("kit/x.cs", "first line\n" + example + "\n", AllRules);
                Assert.True(hits.Any(h => h.Rule == rule.Id && h.Line == 2), $"rule {rule.Id} catches its example: {example}");
            }
        }

        Assert.All(local.LeaveAlone, text => Assert.Empty(PublicContentScanner.Scan("kit/x.cs", text, AllRules)));
    }

    [Fact]
    public void RulesFile_SaysWhyForEachRule()
    {
        Assert.All(Rules.Rules, r => Assert.False(string.IsNullOrWhiteSpace(r.Why), $"rule {r.Id} says why it is there"));
    }
}

/// <summary>PublicContentRules.json, as the guard reads it.</summary>
internal sealed record PublicContentRules(
    IReadOnlyList<string> Roots,
    IReadOnlyList<string> SkipDirectories,
    IReadOnlyList<ContentRule> Rules,
    IReadOnlyList<string> LeaveAlone)
{
    public const string RelativePath = "tests/Repo.Tests/PublicContentRules.json";

    // In the optional local folder private/ (gitignored); the test that reads it skips without it.
    public const string LocalRelativePath = "private/PublicContentRules.private.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static PublicContentRules Load(string path)
    {
        var file = JsonSerializer.Deserialize<RulesFile>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException($"{path} is empty");
        return new PublicContentRules(
            file.Roots ?? [],
            file.SkipDirectories ?? [],
            file.Rules.Select(r => new ContentRule(
                r.Id,
                r.Why,
                new Regex(r.Pattern, RegexOptions.CultureInvariant),
                r.AllowValue is null ? null : new Regex(r.AllowValue, RegexOptions.CultureInvariant),
                (r.AllowAt ?? []).Select(a => (a.Path, new Regex("^(?:" + a.Value + ")$", RegexOptions.CultureInvariant))).ToList(),
                r.Examples ?? [])).ToList(),
            file.LeaveAlone ?? []);
    }

    /// <summary>These rules and another file's, with these roots and skipped folders.</summary>
    public PublicContentRules With(PublicContentRules more) => this with { Rules = [.. Rules, .. more.Rules], LeaveAlone = [.. LeaveAlone, .. more.LeaveAlone] };

    private sealed record RulesFile(List<string>? Roots, List<string>? SkipDirectories, List<RuleEntry> Rules, List<string>? LeaveAlone);

    private sealed record RuleEntry(string Id, string Why, string Pattern, string? AllowValue, List<AllowAtEntry>? AllowAt, List<string>? Examples);

    private sealed record AllowAtEntry(string Path, string Value, string Why);
}

internal sealed record ContentRule(string Id, string Why, Regex Pattern, Regex? AllowValue, IReadOnlyList<(string Path, Regex Value)> AllowAt, IReadOnlyList<string> Examples);

internal sealed record ContentHit(string Path, int Line, string Rule, string Text)
{
    public override string ToString() => $"{Path}:{Line}: [{Rule}] {Text}";
}

/// <summary>The guard's walk and scan.</summary>
internal static class PublicContentScanner
{
    /// <summary>The text files under the roots, relative with '/', skipping build output, sandboxes and binaries.</summary>
    public static IReadOnlyList<string> PublishedFiles(string root, PublicContentRules rules)
    {
        var skip = new HashSet<string>(rules.SkipDirectories, StringComparer.OrdinalIgnoreCase);
        var files = new List<string>();
        foreach (var entry in rules.Roots)
        {
            var full = Path.Combine(root, entry);
            if (File.Exists(full))
            {
                files.Add(entry);
            }
            else if (Directory.Exists(full))
            {
                Walk(root, full, skip, files);
            }
        }

        return files.Where(f => !IsBinary(Path.Combine(root, f))).ToList();
    }

    public static IReadOnlyList<ContentHit> Scan(string relativePath, string text, PublicContentRules rules)
    {
        var hits = new List<ContentHit>();
        ScanInto(hits, relativePath, relativePath, 0, rules, pathOnly: true);
        ScanInto(hits, relativePath, text, 1, rules, pathOnly: false);
        return hits.OrderBy(h => h.Line).ThenBy(h => h.Rule, StringComparer.Ordinal).ToList();
    }

    private static void ScanInto(List<ContentHit> hits, string path, string text, int firstLine, PublicContentRules rules, bool pathOnly)
    {
        var lineStarts = LineStarts(text);
        foreach (var rule in rules.Rules)
        {
            foreach (Match m in rule.Pattern.Matches(text))
            {
                var group = m.Groups["v"];
                var value = group.Success ? group.Value : m.Value;
                if (rule.AllowValue?.IsMatch(value) == true
                    || rule.AllowAt.Any(a => string.Equals(a.Path, path, StringComparison.Ordinal) && a.Value.IsMatch(m.Value)))
                {
                    continue;
                }

                hits.Add(Hit(path, text, lineStarts, m.Index, firstLine, rule.Id, pathOnly));
            }
        }
    }

    private static ContentHit Hit(string path, string text, List<int> lineStarts, int index, int firstLine, string rule, bool pathOnly)
    {
        var line = LineIndex(lineStarts, index);
        var start = lineStarts[line];
        var end = line + 1 < lineStarts.Count ? lineStarts[line + 1] : text.Length;
        var excerpt = text[start..end].Trim();
        if (excerpt.Length > 200)
        {
            var from = Math.Clamp(index - start - 80, 0, excerpt.Length - 200);
            excerpt = string.Concat("...", excerpt.AsSpan(from, 200), "...");
        }

        return new ContentHit(path, pathOnly ? 0 : firstLine + line, rule, excerpt);
    }

    private static List<int> LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                starts.Add(i + 1);
            }
        }

        return starts;
    }

    private static int LineIndex(List<int> lineStarts, int index)
    {
        var found = lineStarts.BinarySearch(index);
        return found >= 0 ? found : ~found - 1;
    }

    private static void Walk(string root, string directory, HashSet<string> skip, List<string> files)
    {
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            files.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
        }

        foreach (var sub in Directory.EnumerateDirectories(directory))
        {
            if (!skip.Contains(Path.GetFileName(sub)))
            {
                Walk(root, sub, skip, files);
            }
        }
    }

    private static bool IsBinary(string path)
    {
        using var stream = File.OpenRead(path);
        var buffer = new byte[8000];
        var read = stream.Read(buffer, 0, buffer.Length);
        return Array.IndexOf(buffer, (byte)0, 0, read) >= 0;
    }
}
