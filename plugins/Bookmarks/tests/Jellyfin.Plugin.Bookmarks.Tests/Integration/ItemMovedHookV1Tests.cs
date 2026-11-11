using System;
using System.Linq;
using System.Reflection;
using Jellyfin.Plugin.Bookmarks.Integration;
using Jellyfin.Plugin.Bookmarks.Tests.Services;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Integration;

/// <summary>
/// The kit's move hook in Bookmarks: a public static facade with BCL types only, located by Library Maintenance
/// by name, that asks the running identity service for a journal catch-up and never fails its caller.
/// </summary>
[Collection(ItemMovedHookCollection.Name)]
public sealed class ItemMovedHookV1Tests
{
    [Fact]
    public void TheFacade_IsPublicStatic_WithOneBclMethod_UnderItsContractName()
    {
        var type = typeof(ItemMovedHookV1);

        Assert.True(type.IsPublic && type.IsAbstract && type.IsSealed);
        Assert.Equal("Jellyfin.Plugin.Bookmarks.Integration.ItemMovedHookV1", type.FullName);
        Assert.Equal("Jellyfin.Plugin.Bookmarks", type.Assembly.GetName().Name);
        var method = Assert.Single(type.GetMethods(BindingFlags.Public | BindingFlags.Static));
        Assert.Equal(nameof(ItemMovedHookV1.OnItemsMoved), method.Name);
        Assert.Equal(typeof(void), method.ReturnType);
        Assert.Equal([typeof(string)], method.GetParameters().Select(p => p.ParameterType));
    }

    [Fact]
    public void ACall_ReachesTheAttachedTarget_NeverThrows_AndADetachedOrReplacedTargetIsLeft()
    {
        var first = 0;
        var second = 0;
        Action firstTarget = () => first++;
        Action secondTarget = () => second++;
        Action throwing = () => throw new InvalidOperationException("the service is stopping");
        try
        {
            ItemMovedHookV1.Attach(firstTarget);
            ItemMovedHookV1.OnItemsMoved("[]");
            Assert.Equal(1, first);

            // A new host (Jellyfin restarts in the same process) replaces the target; the old service's Detach leaves it.
            ItemMovedHookV1.Attach(secondTarget);
            ItemMovedHookV1.Detach(firstTarget);
            ItemMovedHookV1.OnItemsMoved(null!);
            Assert.Equal((1, 1), (first, second));

            ItemMovedHookV1.Attach(throwing);
            ItemMovedHookV1.OnItemsMoved("[]");
            ItemMovedHookV1.Detach(throwing);
            ItemMovedHookV1.OnItemsMoved("[]");
            Assert.Equal((1, 1), (first, second));
        }
        finally
        {
            ItemMovedHookV1.Detach(firstTarget);
            ItemMovedHookV1.Detach(secondTarget);
            ItemMovedHookV1.Detach(throwing);
        }
    }
}
