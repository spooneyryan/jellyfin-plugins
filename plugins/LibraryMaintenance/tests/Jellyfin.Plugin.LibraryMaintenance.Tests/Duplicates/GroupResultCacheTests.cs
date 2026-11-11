using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.LibraryMaintenance.Duplicates.Grouping;
using Jellyfin.Plugin.LibraryMaintenance.Duplicates.Groups;
using Xunit;

namespace Jellyfin.Plugin.LibraryMaintenance.Tests.Duplicates;

/// <summary>
/// The group results' cache: the LRU of 8 results keyed by the snapshot's version, the options' hash and the
/// effective library ids, cleared by a newer snapshot.
/// </summary>
public sealed class GroupResultCacheTests
{
    private static readonly Guid A = new("c0000000-0000-0000-0000-00000000000a");
    private static readonly Guid B = new("c0000000-0000-0000-0000-00000000000b");

    [Fact]
    public void AResult_IsGroupedOnce_PerOptionsAndLibrarySet_WhateverTheLibrariesOrder()
    {
        var cache = new GroupResultCache();
        var calls = 0;
        GroupingResult Group()
        {
            calls++;
            return Empty();
        }

        var first = cache.GetOrAdd(1, "h", [A, B], Group);
        Assert.Same(first, cache.GetOrAdd(1, "h", [B, A, B], Group));
        Assert.Equal(1, calls);

        // Another library set, or other options, is another result: one admin's never reaches another.
        Assert.NotSame(first, cache.GetOrAdd(1, "h", [A], Group));
        Assert.NotSame(first, cache.GetOrAdd(1, "h", [], Group));
        Assert.NotSame(first, cache.GetOrAdd(1, "other", [A, B], Group));
        Assert.Equal(4, calls);
        Assert.Equal((4, 1L, 4L), (cache.Count, cache.Hits, cache.Misses));
        Assert.Equal(GroupResultCache.Key("h", [B, A]), GroupResultCache.Key("h", [A, B, A]));
        Assert.NotEqual(GroupResultCache.Key("h", [A]), GroupResultCache.Key("h", [B]));
    }

    [Fact]
    public void ItKeepsEight_DroppingTheLeastRecentlyUsed()
    {
        var cache = new GroupResultCache();
        var results = Enumerable.Range(0, GroupResultCache.Capacity).Select(i => cache.GetOrAdd(1, "h" + i, [A], Empty)).ToList();

        // h0 is used again, so h1 is the one a ninth result drops.
        Assert.Same(results[0], cache.GetOrAdd(1, "h0", [A], Empty));
        cache.GetOrAdd(1, "h8", [A], Empty);

        Assert.Equal(GroupResultCache.Capacity, cache.Count);
        Assert.Same(results[0], cache.GetOrAdd(1, "h0", [A], Empty));
        Assert.Same(results[2], cache.GetOrAdd(1, "h2", [A], Empty));
        Assert.NotSame(results[1], cache.GetOrAdd(1, "h1", [A], Empty));
    }

    [Fact]
    public void ANewerSnapshot_ClearsIt_AndAnOlderOnesResult_IsAnsweredButNeverKept()
    {
        var cache = new GroupResultCache();
        var old = cache.GetOrAdd(1, "h", [A], Empty);
        cache.GetOrAdd(1, "h2", [A], Empty);

        var newer = cache.GetOrAdd(2, "h", [A], Empty);
        Assert.NotSame(old, newer);
        Assert.Equal(1, cache.Count);

        // A request still on snapshot 1 (its read came before the build ended) groups, and its result isn't kept.
        var late = cache.GetOrAdd(1, "h", [A], Empty);
        Assert.NotSame(newer, late);
        Assert.Same(newer, cache.GetOrAdd(2, "h", [A], Empty));
        Assert.Equal(1, cache.Count);

        // A build that ends while a request groups: its result is for the old snapshot, so it isn't kept.
        GroupingResult Racing()
        {
            cache.GetOrAdd(3, "x", [A], Empty);
            return Empty();
        }

        cache.GetOrAdd(2, "y", [A], Racing);
        Assert.Equal(1, cache.Count);
        var grouped = false;
        cache.GetOrAdd(3, "x", [A], () =>
        {
            grouped = true;
            return Empty();
        });
        Assert.False(grouped);
    }

    private static GroupingResult Empty() => new([], 0, 0, 0);
}
