using Jellyfin.Plugin.LibraryMaintenance.Settings;
using Xunit;

namespace Jellyfin.Plugin.LibraryMaintenance.Tests.Settings;

/// <summary>
/// The settings' path forms (as 1.0.0, 1.0.1 and 1.0.2 tightened them): which excluded folders are device or NT paths (refused, never opened)
/// and which are network paths (kept, never opened before a save).
/// </summary>
public sealed class SettingsRulesTests
{
    [Theory]
    [InlineData(@"\\?\C:\Clips")]
    [InlineData(@"\\.\pipe\clips")]
    [InlineData("//?/C:/Clips")]
    [InlineData("//./C:/Clips")]
    [InlineData(@"\/?\C:\Clips")]
    [InlineData(@"/\./C:\Clips")]
    [InlineData(@"\\?\UNC\nas\movies")]
    [InlineData(@"\\.\UNC\nas\movies")]
    [InlineData(@"\\?\GLOBALROOT\Device\Mup\nas\movies")]
    [InlineData(@"\\.\GLOBALROOT\Device\Mup\nas\movies")]
    [InlineData(@"\\?\Volume{00000000-0000-0000-0000-000000000000}\Clips")]
    [InlineData(@"\\?")]
    [InlineData(@"\\.")]
    [InlineData(@"\\?\")]
    // The NT object prefix, which Win32 hands to the object manager as it is.
    [InlineData(@"\??\UNC\x\y")]
    [InlineData(@"\??\UNC\nas\movies")]
    [InlineData(@"\??\C:\x")]
    [InlineData(@"\??\C:/x")]
    [InlineData(@"\??\GLOBALROOT\Device\Mup\nas\movies")]
    [InlineData(@"\??\GLOBALROOT\??\C:\x")]
    [InlineData(@"\??\")]
    [InlineData(@"\??")]
    // Its slash spellings: Win32 reads them as a folder on the current drive (C:\??\...), but none is a folder path.
    [InlineData("/??/C:/x")]
    [InlineData(@"\??/C:/x")]
    [InlineData(@"/??\UNC\x\y")]
    // Any path whose second character is ?, as Move's PathRules (the 1.0.1 verifier): .NET takes \?\C:\x for fully
    // qualified, and Win32 resolves it to C:\, so a save kept it.
    [InlineData(@"\?\C:\x")]
    [InlineData(@"\?\UNC\host\share")]
    [InlineData(@"\?")]
    [InlineData("/?/x")]
    [InlineData("a?")]
    public void ADeviceOrNtPath_IsADevicePath_AndNotANetworkPath(string path)
    {
        Assert.True(SettingsRules.IsDevicePath(path));
        Assert.False(SettingsRules.IsNetworkPath(path));
    }

    [Theory]
    [InlineData(@"\\nas\movies", true)]
    [InlineData("//nas/movies", true)]
    [InlineData(@"\\??\UNC\x\y", true)]
    [InlineData(@"\\?x\movies", true)]
    [InlineData(@"\\.x\movies", true)]
    [InlineData(@"\\..\movies", true)]
    [InlineData(@"D:\Clips", false)]
    [InlineData("D:/Clips", false)]
    [InlineData(@"D:\Movies\?? Unknown", false)]
    [InlineData(@"C:\??\C:\x", false)]
    [InlineData(@"\.\C:\x", false)]
    [InlineData(@"\Clips", false)]
    [InlineData("/mnt/movies", false)]
    [InlineData("?", false)]
    [InlineData("", false)]
    [InlineData(@"\", false)]
    public void AnotherForm_IsNotADevicePath(string path, bool network)
    {
        // The negative controls: a UNC path stays a network path (kept, never opened before a save); a local folder,
        // a Linux one and a lone ? are neither (the full-path rule and Win32 refuse what isn't a folder).
        Assert.False(SettingsRules.IsDevicePath(path));
        Assert.Equal(network, SettingsRules.IsNetworkPath(path));
    }

    [Fact]
    public void Null_IsNeither()
    {
        Assert.False(SettingsRules.IsDevicePath(null!));
        Assert.False(SettingsRules.IsNetworkPath(null!));
    }
}
