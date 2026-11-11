using System;
using System.IO;

namespace Jellyfin.Plugin.Kit.TestSupport;

/// <summary>
/// A unique temporary directory under <c>%TEMP%\jellyfin-plugin-tests</c> that is deleted on dispose.
/// </summary>
public sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "jellyfin-plugin-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public string CreateFile(string name, string contents = "data")
    {
        var path = Combine(name);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Best effort; a lingering process may still hold a file.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort, as above.
        }
    }
}
