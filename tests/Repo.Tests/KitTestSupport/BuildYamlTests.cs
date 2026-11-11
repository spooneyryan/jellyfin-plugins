using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Kit.TestSupport.PluginIdentity;
using Xunit;

namespace Jellyfin.Plugins.Repo.Tests.KitTestSupport;

public sealed class BuildYamlTests
{
    private const string BookmarksStyle = """
        ---
        name: "Bookmarks"
        guid: "d0ee1b2a-7f71-4654-8ff2-163a81441b03"
        version: "1.0.0.0"
        targetAbi: "10.11.5.0"
        framework: "net9.0"
        overview: "Per-user timestamp bookmarks"
        description: >
          Lets each user bookmark moments,
          optionally with an end time.

          Second paragraph.
        category: "General"
        owner: "Example Owner"
        artifacts:
        - "Jellyfin.Plugin.Bookmarks.dll"
        changelog: >
          Initial release.
        """;

    [Fact]
    public void Parse_ReadsAJprmBuildFile()
    {
        var yaml = BuildYaml.Parse(BookmarksStyle);

        Assert.Equal("Bookmarks", yaml.Name);
        Assert.Equal("d0ee1b2a-7f71-4654-8ff2-163a81441b03", yaml.Guid);
        Assert.Equal("1.0.0.0", yaml.Version);
        Assert.Equal("10.11.5.0", yaml.TargetAbi);
        Assert.Equal("net9.0", yaml.Framework);
        Assert.Equal(["Jellyfin.Plugin.Bookmarks.dll"], yaml.Artifacts);
        Assert.Equal("Lets each user bookmark moments, optionally with an end time.\nSecond paragraph.\n", yaml.GetScalar("description"));
        Assert.Equal("Initial release.\n", yaml.GetScalar("changelog"));
        Assert.Empty(PluginIdentityChecks.CheckRequiredFields(yaml));
    }

    [Fact]
    public void Parse_HandlesQuotingCommentsAndIndentedLists()
    {
        var yaml = BuildYaml.Parse("""
            # comment
            name: 'It''s # not a comment'   # a comment
            owner: plain value # trailing
            overview: "say \"hi\""
            artifacts:
              - "A.dll"   # first
              - B.dll
            """);

        Assert.Equal("It's # not a comment", yaml.Name);
        Assert.Equal("plain value", yaml.GetScalar("owner"));
        Assert.Equal("say \"hi\"", yaml.GetScalar("overview"));
        Assert.Equal(["A.dll", "B.dll"], yaml.Artifacts);
    }

    [Fact]
    public void Parse_LiteralBlockKeepsLineBreaks_AndStripChompingDropsTheLastOne()
    {
        var yaml = BuildYaml.Parse("changelog: |-\n  - one\n  - two\nname: x\n");

        Assert.Equal("- one\n- two", yaml.GetScalar("changelog"));
        Assert.Equal("x", yaml.Name);
    }

    [Fact]
    public void Parse_AcceptsWindowsLineEndings()
    {
        var yaml = BuildYaml.Parse("name: \"A\"\r\nartifacts:\r\n- \"A.dll\"\r\n");

        Assert.Equal("A", yaml.Name);
        Assert.Equal(["A.dll"], yaml.Artifacts);
    }

    [Theory]
    [InlineData("name: a\nname: b\n")]
    [InlineData("nested:\n  key: value\n")]
    [InlineData("  indented: value\n")]
    [InlineData("name:value\n")]
    [InlineData("artifacts:\n- { a: 1 }\n")]
    [InlineData("artifacts:\n- a: 1\n")]
    public void Parse_RejectsWhatBuildYamlDoesNotUse(string text)
    {
        Assert.Throws<FormatException>(() => BuildYaml.Parse(text));
    }

    [Fact]
    public void Require_ThrowsForMissingOrEmptyValues()
    {
        var yaml = BuildYaml.Parse("name: \"\"\n");

        Assert.Throws<KeyNotFoundException>(() => yaml.Name);
        Assert.Throws<KeyNotFoundException>(() => yaml.Guid);
        Assert.False(yaml.Has("name"));
        Assert.Empty(yaml.Artifacts);
        Assert.Contains("build.yaml has no 'guid'.", PluginIdentityChecks.CheckRequiredFields(yaml));
    }
}
