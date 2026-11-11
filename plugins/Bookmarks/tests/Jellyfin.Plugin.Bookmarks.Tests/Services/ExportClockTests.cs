using System;
using System.Linq;
using System.Reflection;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.Kit.TestSupport.Api;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Services;

/// <summary>
/// Bookmarks 1.2.1: every export of Bookmarks' (the bookmarks, the user data demo, the library demo) dates its file by the
/// server's <see cref="TimeProvider"/>, as User Ratings' does, never by the envelope's default of the real clock: in the
/// compiled code, each method that calls <see cref="ExportEnvelope.Create"/> reads <see cref="TimeProvider.GetUtcNow"/>.
/// </summary>
public sealed class ExportClockTests
{
    [Fact]
    public void EveryExport_DatesItsFileByTheServersClock()
    {
        var exports = typeof(Plugin).Assembly.GetTypes()
            .Where(t => t.Namespace is { } ns && ns.StartsWith("Jellyfin.Plugin.Bookmarks", StringComparison.Ordinal))
            .SelectMany(MethodBodyReferences.DeclaredMethods)
            .Where(m => MethodBodyReferences.FindMembers(m).Any(IsCreate))
            .ToList();

        Assert.Equal(3, exports.Count);
        Assert.All(exports, m => Assert.True(
            MethodBodyReferences.FindMembers(m).Any(r => r.DeclaringType == typeof(TimeProvider) && r.Name == nameof(TimeProvider.GetUtcNow)),
            m.DeclaringType!.Name + "." + m.Name + " calls ExportEnvelope.Create without the server's clock"));
    }

    private static bool IsCreate(MemberInfo member)
        => member.DeclaringType == typeof(ExportEnvelope) && member.Name == nameof(ExportEnvelope.Create);
}
