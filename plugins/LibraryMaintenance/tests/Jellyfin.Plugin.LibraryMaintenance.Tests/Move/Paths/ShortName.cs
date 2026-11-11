using System;
using System.IO;
using System.Runtime.InteropServices;
using Xunit;

namespace Jellyfin.Plugin.LibraryMaintenance.Tests.Move.Paths;

/// <summary>
/// A real 8.3 short name for a test (<c>GetShortPathNameW</c>), or a skip when the test volume doesn't make them
/// (<c>fsutil 8dot3name</c> off), so a test never passes on a name that isn't an alias.
/// </summary>
internal static class ShortName
{
    /// <summary>
    /// Gets the short name Windows gave an entry, skipping the test when it has none.
    /// </summary>
    /// <param name="path">An existing file or folder whose long name isn't a valid 8.3 name.</param>
    /// <returns>Its short name, such as <c>HEAT(1~1</c>.</returns>
    public static string Of(string path)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "8.3 names are a Windows feature.");
        var buffer = new char[1024];
        var length = GetShortPathName(path, buffer, (uint)buffer.Length);
        var name = length == 0 || length > buffer.Length ? string.Empty : Path.GetFileName(new string(buffer, 0, (int)length));
        Assert.SkipWhen(
            name.Length == 0 || string.Equals(name, Path.GetFileName(path), StringComparison.OrdinalIgnoreCase),
            "The test volume gives " + path + " no 8.3 short name (8.3 names are off there), so there is no alias to test.");
        return name;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetShortPathNameW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetShortPathName(string longPath, [Out] char[] shortPath, uint bufferLength);
}
