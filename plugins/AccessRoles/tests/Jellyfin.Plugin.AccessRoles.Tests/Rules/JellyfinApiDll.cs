using System;
using System.IO;

namespace Jellyfin.Plugin.AccessRoles.Tests.Rules;

/// <summary>
/// Where the manifest test finds the <c>Jellyfin.Api.dll</c> it checks the manifest against.
/// Jellyfin.Api has no NuGet package, so the DLL comes from outside the build:
/// <list type="number">
/// <item><description><c>JELLYFIN_API_DLL</c>, a path: what CI sets after building <c>Jellyfin.Api</c> from a shallow checkout
/// of the tag (<c>plugins/AccessRoles/DEVELOPMENT.md</c>, "The action manifest"). When it is set, the file must exist.</description></item>
/// <item><description>Otherwise the installed server's copy, <c>C:\ProgramData\Jellyfin\Server\Jellyfin.Api.dll</c>, read only
/// and never executed (only its metadata is parsed).</description></item>
/// </list>
/// With neither, or with an installed copy of another version, a local run skips the test and says so; in CI (<see
/// cref="CiVariable"/> set) it fails instead, so a CI job can't pass without the check.
/// </summary>
internal static class JellyfinApiDll
{
    /// <summary>The environment variable naming the DLL.</summary>
    public const string Variable = "JELLYFIN_API_DLL";

    /// <summary>The environment variable CI services set (GitHub Actions, GitLab, Azure Pipelines and others: <c>true</c>).</summary>
    public const string CiVariable = "CI";

    /// <summary>The installed server's copy on this machine.</summary>
    public const string InstalledServerCopy = @"C:\ProgramData\Jellyfin\Server\Jellyfin.Api.dll";

    /// <summary>
    /// Gets a value indicating whether the tests run in CI, where the manifest check must not be skipped.
    /// </summary>
    /// <returns><c>true</c> when <see cref="CiVariable"/> is set to anything but <c>false</c> or <c>0</c>.</returns>
    public static bool InCi() => InCi(Environment.GetEnvironmentVariable(CiVariable));

    /// <summary>
    /// Reads a value of <see cref="CiVariable"/>.
    /// </summary>
    /// <param name="value">The value, or <c>null</c> when the variable isn't set.</param>
    /// <returns><c>true</c> for any value but empty, <c>false</c> (any case) or <c>0</c>.</returns>
    public static bool InCi(string? value)
        => !string.IsNullOrWhiteSpace(value)
            && !string.Equals(value.Trim(), "false", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(value.Trim(), "0", StringComparison.Ordinal);

    /// <summary>
    /// Finds the DLL.
    /// </summary>
    /// <returns>The path, or <c>null</c> when there is none to check against.</returns>
    /// <exception cref="FileNotFoundException"><see cref="Variable"/> names a file that doesn't exist.</exception>
    public static string? Locate()
    {
        var configured = Environment.GetEnvironmentVariable(Variable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return File.Exists(configured)
                ? Path.GetFullPath(configured)
                : throw new FileNotFoundException($"{Variable} names {configured}, which doesn't exist.", configured);
        }

        return File.Exists(InstalledServerCopy) ? InstalledServerCopy : null;
    }
}
