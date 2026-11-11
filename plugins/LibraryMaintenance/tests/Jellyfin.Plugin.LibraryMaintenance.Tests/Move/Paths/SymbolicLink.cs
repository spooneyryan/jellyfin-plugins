using System;
using System.IO;
using Xunit;

namespace Jellyfin.Plugin.LibraryMaintenance.Tests.Move.Paths;

/// <summary>
/// Makes a symbolic link for a test, inside the test's temp directory and to a target there. It needs Developer Mode or the
/// symbolic-link privilege; without either the test is skipped.
/// </summary>
internal static class SymbolicLink
{
    public static void ToFile(string link, string target) => Create(() => File.CreateSymbolicLink(link, target));

    public static void ToFolder(string link, string target) => Create(() => Directory.CreateSymbolicLink(link, target));

    private static void Create(Action create)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Symbolic links are tested on Windows.");
        try
        {
            create();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("This account can't make symbolic links (" + e.Message + ").");
        }
    }
}
