using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.TestSupport.PluginIdentity;
using Xunit;

namespace Jellyfin.Plugins.Repo.Tests;

/// <summary>
/// The plugins' images: each plugin folder's image.png (rendered from branding/), and how Deploy-Plugin.ps1 and
/// Install-RealServer.ps1 install it: copied as image.png next to the DLLs, checked by SHA-256, named in meta.json's
/// imagePath (which Jellyfin 10.11.5 serves at GET /Plugins/{id}/{version}/Image), and recorded in backup.json and
/// RESTORE.txt. A release tag's image when it has one, or else the one committed at the scripts' checkout's HEAD, so that
/// reinstalling a release tagged before the plugins had images gets one. On ReleaseInstallTests' ReleaseRepository.
/// </summary>
public sealed class PluginImageTests
{
    private static readonly string Root = PluginIdentityChecks.FindRepositoryRoot();

    // Bytes a text conversion would change (CR LF, a lone LF, NUL, Ctrl+Z, bytes above 127): the image must arrive as it is.
    private static readonly byte[] TagImage = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01, 0xFF, 0x0A, 0x0D, 0x80, 0x54, 0x41, 0x47];

    private static readonly byte[] HeadImage = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x02, 0xFE, 0x0D, 0x0A, 0x0A, 0x48, 0x45, 0x41, 0x44];

    private static readonly byte[] WorkingImage = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x03, 0x57, 0x54];

    [Fact]
    public void EachPlugin_HasItsImage_A1280x720Png_RenderedFromBranding()
    {
        var plugins = Directory.GetDirectories(Path.Combine(Root, "plugins")).Where(d => File.Exists(Path.Combine(d, "build.yaml"))).Select(Path.GetFileName).ToList();
        Assert.Equal(6, plugins.Count);
        var render = File.ReadAllText(Path.Combine(Root, "branding", "render.mjs"));
        foreach (var plugin in plugins)
        {
            var (width, height) = PngSize(Path.Combine(Root, "plugins", plugin!, "image.png"));
            Assert.True(width == 1280 && height == 720, $"{plugin}'s image.png is {width}x{height}");
            Assert.True(File.Exists(Path.Combine(Root, "branding", plugin + ".svg")), $"branding/{plugin}.svg");
            Assert.Contains($"  {plugin}: 'plugins/{plugin}/image.png',", render, StringComparison.Ordinal);
        }

        Assert.Equal((1000, 1500), PngSize(Path.Combine(Root, "plugins", "SmartSearch", "Jellyfin.Plugin.SmartSearch", "AutoCollections", "AutoCollection.png")));
        Assert.Contains("*.png binary", File.ReadAllText(Path.Combine(Root, ".gitattributes")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deploy_InstallsTheWorkingTreesImage_ChecksItBySha256_AndNamesItInMetaJson_AndWithoutOneWritesNoImagePath()
    {
        using var repo = await ReleaseRepository.CreateAsync();
        await repo.CommitAsync("1.0.0.1", "work", await ReleaseRepository.CompileAsync(repo.Combine("work"), "1.0.0.1", "Work"));
        WriteImage(repo, WorkingImage);

        var with = await repo.DeployAsync(string.Empty);
        Assert.True(with.ExitCode == 0, with.Output);
        Assert.Equal("installed", with.Lines[^1]);
        var installed = Path.Combine(repo.Data, "plugins", "RefCheck_1.0.0.1");
        Assert.Equal(WorkingImage, await File.ReadAllBytesAsync(Path.Combine(installed, "image.png"), TestContext.Current.CancellationToken));
        Assert.Equal("image.png", MetaJson(installed).GetProperty("imagePath").GetString());
        var hash = Sha256(WorkingImage);
        Assert.Contains($@"host: Image: image.png from the working tree of {repo.Repo} (plugins/RefCheck/image.png), SHA-256 {hash}", with.Lines);
        Assert.Contains(with.Lines, l => l.StartsWith("host: Checked by SHA-256: Jellyfin.Plugin.RefCheck.dll ", StringComparison.Ordinal) && l.EndsWith($", image.png {hash}", StringComparison.Ordinal));

        // No image: none installed and no imagePath, as before the plugins had images (Jellyfin then shows its placeholder).
        File.Delete(Path.Combine(repo.Repo, "plugins", "RefCheck", "image.png"));
        var without = await repo.DeployAsync(string.Empty);
        Assert.True(without.ExitCode == 0, without.Output);
        Assert.Contains("host: Image: none (no plugins/RefCheck/image.png to install)", without.Lines);
        Assert.False(File.Exists(Path.Combine(installed, "image.png")));
        Assert.False(MetaJson(installed).TryGetProperty("imagePath", out _));
        Assert.Equal(["Jellyfin.Plugin.RefCheck.dll", "meta.json"], Directory.GetFiles(installed).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ReleaseInstall_OfATagWithoutAnImage_InstallsTheOneCommittedAtHead_NotTheWorkingTrees_AndRecordsWhichItWas()
    {
        using var repo = await ReleaseRepository.CreateAsync();
        var released = await ReleaseRepository.CompileAsync(repo.Combine("released"), "1.0.0.0", "Released");
        var tagged = await repo.CommitAsync("1.0.0.0", "1.0.0", released);
        await repo.TagAsync("refcheck/v1.0.0");
        WriteImage(repo, HeadImage);
        var head = await repo.CommitAsync("1.0.0.0", "the image, after the release", released);

        // An uncommitted change to the image: the install reads HEAD's from git, never the working tree's.
        WriteImage(repo, WorkingImage);
        var hash = Sha256(HeadImage);
        var from = $"the committed HEAD of {repo.Repo} (commit {head}), as refcheck/v1.0.0 has no plugins/RefCheck/image.png";

        var plan = await repo.InstallAsync("-WhatIf");
        Assert.True(plan.ExitCode == 0, plan.Output);
        Assert.Contains($"host: Image: image.png from {from}, SHA-256 {hash}", plan.Lines);
        Assert.False(Directory.Exists(repo.Backups));

        var run = await repo.InstallAsync(string.Empty);
        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Equal("installed", run.Lines[^1]);
        Assert.Equal(2, run.Lines.Count(l => l == $"host: Image: image.png from {from}, SHA-256 {hash}"));

        var installed = Path.Combine(repo.Data, "plugins", "RefCheck_1.0.0.0");
        Assert.Equal(HeadImage, await File.ReadAllBytesAsync(Path.Combine(installed, "image.png"), TestContext.Current.CancellationToken));
        Assert.Equal("image.png", MetaJson(installed).GetProperty("imagePath").GetString());

        var backup = Assert.Single(Directory.GetDirectories(repo.Backups));
        using (var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(backup, "backup.json"), TestContext.Current.CancellationToken)))
        {
            Assert.Equal(tagged, manifest.RootElement.GetProperty("commit").GetString());
            var installedRecord = manifest.RootElement.GetProperty("installed");
            var files = installedRecord.GetProperty("files").EnumerateArray().ToList();
            Assert.Equal(["Jellyfin.Plugin.RefCheck.dll", "image.png"], files.Select(f => f.GetProperty("name").GetString()));
            Assert.Equal(hash, files[1].GetProperty("sha256").GetString());
            Assert.Equal(HeadImage.Length, files[1].GetProperty("bytes").GetInt64());
            var image = installedRecord.GetProperty("image");
            Assert.Equal("image.png", image.GetProperty("name").GetString());
            Assert.Equal("Head", image.GetProperty("source").GetString());
            Assert.Equal(head, image.GetProperty("commit").GetString());
            Assert.Equal(from, image.GetProperty("from").GetString());
            Assert.Equal(hash, image.GetProperty("sha256").GetString());
        }

        var restore = (await File.ReadAllTextAsync(Path.Combine(backup, "RESTORE.txt"), TestContext.Current.CancellationToken)).Split("\r\n");
        Assert.EndsWith($", image.png {hash}.", restore[2], StringComparison.Ordinal);
        Assert.Equal($"   Its image, image.png, came from {from}.", restore[3]);

        // The checkout is as it was: the working tree's change still there, nothing committed.
        Assert.Equal(head, (await repo.GitAsync("rev-parse", "HEAD")).Trim());
        Assert.Equal(WorkingImage, await File.ReadAllBytesAsync(Path.Combine(repo.Repo, "plugins", "RefCheck", "image.png"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReleaseInstall_OfATagWithAnImage_InstallsTheTagsOwn_AndATagWithoutAndNoneAtHeadInstallsNone()
    {
        using var repo = await ReleaseRepository.CreateAsync();
        var bare = await repo.CommitAsync("1.0.0.0", "1.0.0", await ReleaseRepository.CompileAsync(repo.Combine("v1.0"), "1.0.0.0", "V10"));
        await repo.TagAsync("refcheck/v1.0.0");
        WriteImage(repo, TagImage);
        var v1_1 = await ReleaseRepository.CompileAsync(repo.Combine("v1.1"), "1.1.0.0", "V11");
        var tagged = await repo.CommitAsync("1.1.0.0", "1.1.0", v1_1);
        await repo.TagAsync("refcheck/v1.1.0");
        WriteImage(repo, HeadImage);
        await repo.CommitAsync("1.1.0.0", "a new image, unreleased", v1_1);

        var run = await repo.InstallAsync("-Version 1.1.0");
        Assert.True(run.ExitCode == 0, run.Output);
        var from = $"refcheck/v1.1.0 (commit {tagged})";
        Assert.Contains($"host: Image: image.png from {from}, SHA-256 {Sha256(TagImage)}", run.Lines);
        var installed = Path.Combine(repo.Data, "plugins", "RefCheck_1.1.0.0");
        Assert.Equal(TagImage, await File.ReadAllBytesAsync(Path.Combine(installed, "image.png"), TestContext.Current.CancellationToken));
        var backup = Assert.Single(Directory.GetDirectories(repo.Backups));
        using (var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(backup, "backup.json"), TestContext.Current.CancellationToken)))
        {
            var image = manifest.RootElement.GetProperty("installed").GetProperty("image");
            Assert.Equal("Tag", image.GetProperty("source").GetString());
            Assert.Equal(tagged, image.GetProperty("commit").GetString());
        }

        Assert.Contains($"   Its image, image.png, came from {from}.", (await File.ReadAllTextAsync(Path.Combine(backup, "RESTORE.txt"), TestContext.Current.CancellationToken)).Split("\r\n"));

        // Neither the tag nor HEAD has one: nothing installed, no imagePath, nothing recorded.
        await repo.GitAsync("rm", "-q", "plugins/RefCheck/image.png");
        await repo.GitAsync("commit", "-q", "-m", "no image");
        Directory.Delete(repo.Backups, recursive: true);
        var none = await repo.InstallAsync("-Version 1.0.0");
        Assert.True(none.ExitCode == 0, none.Output);
        Assert.Equal("installed", none.Lines[^1]);
        Assert.Contains("host: Image: none (no plugins/RefCheck/image.png in refcheck/v1.0.0 or at this checkout's HEAD)", none.Lines);
        var older = Path.Combine(repo.Data, "plugins", "RefCheck_1.0.0.0");
        Assert.False(File.Exists(Path.Combine(older, "image.png")));
        Assert.False(MetaJson(older).TryGetProperty("imagePath", out _));
        var noneBackup = Assert.Single(Directory.GetDirectories(repo.Backups));
        using (var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(noneBackup, "backup.json"), TestContext.Current.CancellationToken)))
        {
            Assert.Equal(bare, manifest.RootElement.GetProperty("commit").GetString());
            Assert.Equal(JsonValueKind.Null, manifest.RootElement.GetProperty("installed").GetProperty("image").ValueKind);
        }

        Assert.DoesNotContain((await File.ReadAllTextAsync(Path.Combine(noneBackup, "RESTORE.txt"), TestContext.Current.CancellationToken)).Split("\r\n"), l => l.Contains("Its image", StringComparison.Ordinal));
    }

    private static void WriteImage(ReleaseRepository repo, byte[] bytes) =>
        File.WriteAllBytes(Path.Combine(repo.Repo, "plugins", "RefCheck", "image.png"), bytes);

    private static JsonElement MetaJson(string folder) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "meta.json"))).RootElement.Clone();

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static (int Width, int Height) PngSize(string path)
    {
        var bytes = File.ReadAllBytes(path);
        Assert.True(bytes.Length > 24 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }), path + " is not a PNG");
        Assert.True(bytes.Length < 1024 * 1024, $"{path} is {bytes.Length} bytes");
        return ((bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19], (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23]);
    }
}
