using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.LibraryMaintenance.Move.Paths;
using Xunit;

namespace Jellyfin.Plugin.LibraryMaintenance.Tests.Move.Paths;

/// <summary>
/// A path's volume by its GUID (<c>GetVolumePathNameW</c>, which follows junctions, then
/// <c>GetVolumeNameForVolumeMountPointW</c>), checked against <c>mountvol</c>; a second volume mocked (nothing on D: is
/// asked of Windows); device, network and mapped-drive paths never passed to Win32.
/// </summary>
public sealed partial class VolumeInfoProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void GetVolume_OfATempFolder_IsItsDrivesVolumeGuid_AsMountvolSaysIt()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Volume GUIDs are a Windows feature.");
        var volume = new VolumeInfoProvider(static _ => false, []).GetVolume(_temp.Path);

        Assert.NotNull(volume);
        var root = Path.GetPathRoot(_temp.Path)!;
        Assert.Equal(root, volume.MountPoint, ignoreCase: true);
        Assert.Matches(VolumeGuid(), volume.VolumeId);
        Assert.Equal(Mountvol(root), volume.VolumeId, ignoreCase: true);
        Assert.True(volume.FreeBytes > 0);
    }

    [Fact]
    public void GetVolume_OfAFolderNotMadeYet_IsTheNearestExistingFolders()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Volume GUIDs are a Windows feature.");
        var provider = new VolumeInfoProvider(static _ => false, []);
        var existing = provider.GetVolume(_temp.Path);
        var planned = provider.GetVolume(_temp.Combine("New", "Deeper", "Heat (1995)"));
        Assert.NotNull(planned);
        Assert.Equal(existing!.VolumeId, planned.VolumeId);
    }

    [Fact]
    public void GetVolume_ThroughAJunction_IsTheTargetsVolume()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Junctions are a Windows feature.");
        Directory.CreateDirectory(_temp.Combine("Target"));
        Junction.Create(_temp.Combine("Link"), _temp.Combine("Target"));
        var provider = new VolumeInfoProvider(static _ => false, []);

        var through = provider.GetVolume(_temp.Combine("Link"));
        Assert.NotNull(through);
        Assert.Equal(provider.GetVolume(_temp.Combine("Target"))!.VolumeId, through.VolumeId);
    }

    [Fact]
    public void GetVolume_TellsCAndAMockedDApart_WithoutAskingWindowsAboutD()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Volume GUIDs are a Windows feature.");
        // The mapped-drive check is the test's own answer, so D: is never asked of Windows (DriveInfo), and the made-up
        // volume answers before any Win32 call.
        var provider = new VolumeInfoProvider(static _ => false, [(@"D:\", "d")]);

        var c = provider.GetVolume(_temp.Path);
        var d = provider.GetVolume(@"D:\Video\Movies\Heat (1995)");
        var dRoot = provider.GetVolume("d:/");

        Assert.NotNull(c);
        Assert.NotNull(d);
        Assert.Matches(VolumeGuid(), c.VolumeId);
        Assert.Equal(@"\\?\Volume{jflm-test-d}\", d.VolumeId);
        Assert.Equal(@"D:\", d.MountPoint);
        Assert.Null(d.FreeBytes);
        Assert.Equal(d.VolumeId, dRoot!.VolumeId);
        Assert.NotEqual(c.VolumeId, d.VolumeId);

        Assert.Equal("test d", d.Label);
    }

    [Fact]
    public void GetVolume_AMockedVolumeInsideC_IsItsOwn()
    {
        var provider = new VolumeInfoProvider(static _ => false, [(_temp.Combine("Second"), "second")]);
        Assert.Equal(@"\\?\Volume{jflm-test-second}\", provider.GetVolume(_temp.Combine("second", "Movies"))?.VolumeId);
        Assert.Equal(@"\\?\Volume{jflm-test-second}\", provider.GetVolume(_temp.Combine("Second"))?.VolumeId);
        Assert.NotEqual(@"\\?\Volume{jflm-test-second}\", provider.GetVolume(_temp.Combine("Second2"))?.VolumeId);
    }

    [Theory]
    [InlineData(@"\\?\{0}")]
    [InlineData(@"\??\{0}")]
    [InlineData(@"\\.\{0}")]
    [InlineData(@"\\localhost\C$\Windows")]
    [InlineData("//localhost/C$/Windows")]
    [InlineData(@"\?\{0}")]
    [InlineData("Movies")]
    [InlineData("C:Movies")]
    [InlineData("")]
    public void GetVolume_IsNull_ForPathsNeverPassedToWindows(string format)
    {
        var path = string.Format(System.Globalization.CultureInfo.InvariantCulture, format, _temp.Path);
        Assert.Null(new VolumeInfoProvider(static _ => false, []).GetVolume(path));
    }

    [Fact]
    public void GetVolume_IsNull_ForAMappedNetworkDrive()
        => Assert.Null(new VolumeInfoProvider(static _ => true, []).GetVolume(_temp.Path));

    [GeneratedRegex(@"^\\\\\?\\Volume\{[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\}\\$")]
    private static partial Regex VolumeGuid();

    // mountvol's answer for a drive root (read-only: "mountvol C:\ /L" lists the volume's GUID path).
    private static string Mountvol(string root)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "mountvol.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        start.ArgumentList.Add(root);
        start.ArgumentList.Add("/L");
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return output;
    }
}
