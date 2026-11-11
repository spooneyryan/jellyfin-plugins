using Xunit;

namespace Jellyfin.Plugin.AccessRoles.Tests.Rules;

/// <summary>
/// When the manifest test may skip (<see cref="JellyfinApiDll"/>): locally without the reviewed DLL, never in CI, where
/// <c>CI</c> is set.
/// </summary>
public sealed class JellyfinApiDllTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("  ", false)]
    [InlineData("false", false)]
    [InlineData("False", false)]
    [InlineData("0", false)]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("1", true)]
    [InlineData("yes", true)]
    public void InCi_IsAnyValueOfCi_ButFalseOrZero(string? value, bool expected)
    {
        Assert.Equal(expected, JellyfinApiDll.InCi(value));
    }
}
