using System;
using Jellyfin.Plugin.Kit.Library;
using MediaBrowser.Controller.Entities.Movies;
using Xunit;

namespace Jellyfin.Plugin.Kit.Tests.Library;

/// <summary>
/// <see cref="RelinkPolicy"/>: the default and Exact-only policies, and Bookmarks' runtime check
/// (within 2 %, at most 60 s; optionally failing a record without a stored runtime).
/// </summary>
public sealed class RelinkPolicyTests
{
    [Fact]
    public void Default_AllowsStrong_WithoutACheck()
    {
        Assert.True(RelinkPolicy.Default.AllowStrongRelink);
        Assert.Null(RelinkPolicy.Default.Validate);
        Assert.True(RelinkPolicy.Default.Accepts(Snapshot(60), Movie(90)));
    }

    [Fact]
    public void ExactOnly_DisallowsStrong()
    {
        Assert.False(RelinkPolicy.ExactOnly.AllowStrongRelink);
        Assert.Null(RelinkPolicy.ExactOnly.Validate);
    }

    [Theory]
    [InlineData(60, 60, true)]
    [InlineData(60, 61, true)] // 1 s of 60 s is under 2 % (1.2 s)
    [InlineData(60, 62, false)]
    [InlineData(60, 90, false)] // the edition pair of the sandbox media
    [InlineData(7200, 7259, true)] // 2 % of two hours is 144 s, capped at 60 s
    [InlineData(7200, 7261, false)]
    [InlineData(7200, 7141, true)]
    [InlineData(7200, 7139, false)]
    public void RuntimeWithin_TwoPercentAtMostSixtySeconds(int storedSeconds, int candidateSeconds, bool passes)
        => Assert.Equal(passes, RelinkPolicy.RuntimeWithin().Accepts(Snapshot(storedSeconds), Movie(candidateSeconds)));

    [Fact]
    public void RuntimeWithin_WithoutAStoredRuntime_AcceptsAnyCandidate()
    {
        // A JE import that never attached has no runtime.
        var policy = RelinkPolicy.RuntimeWithin();

        Assert.True(policy.Accepts(new IdentitySnapshot { Kind = "Movie" }, Movie(90)));
        Assert.True(policy.Accepts(Snapshot(0), Movie(90)));
    }

    [Fact]
    public void RuntimeWithin_RequiringAStoredRuntime_FailsARecordWithoutOne_AndChecksTheOthersAlike()
    {
        // Bookmarks' own records were made on one file; without its runtime the other copy can't be checked.
        var strict = RelinkPolicy.RuntimeWithin(requireStoredRuntime: true);

        Assert.True(strict.AllowStrongRelink);
        Assert.False(strict.Accepts(new IdentitySnapshot { Kind = "Movie" }, Movie(90)));
        Assert.False(strict.Accepts(Snapshot(0), Movie(90)));
        Assert.True(strict.Accepts(Snapshot(60), Movie(61)));
        Assert.False(strict.Accepts(Snapshot(60), Movie(90)));
        Assert.False(strict.Accepts(Snapshot(60), Movie(0)));
    }

    [Fact]
    public void RuntimeWithin_ACandidateWithoutARuntime_FailsForNow()
    {
        // The scan may still be probing it; the record is re-resolved when the item is updated.
        Assert.False(RelinkPolicy.RuntimeWithin().Accepts(Snapshot(60), Movie(0)));
        Assert.False(RelinkPolicy.RuntimeMatches(600_000_000, null, 0.02, TimeSpan.FromSeconds(60)));
    }

    [Fact]
    public void RuntimeWithin_OtherLimits()
    {
        var loose = RelinkPolicy.RuntimeWithin(0.5, TimeSpan.FromMinutes(10));

        Assert.True(loose.Accepts(Snapshot(60), Movie(90)));
        Assert.False(loose.Accepts(Snapshot(60), Movie(91)));
        Assert.Throws<ArgumentOutOfRangeException>(() => RelinkPolicy.RuntimeWithin(-0.1));
        Assert.Throws<ArgumentOutOfRangeException>(() => RelinkPolicy.RuntimeWithin(0.02, TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void AValidatorSeesTheSnapshotAndTheCandidate()
    {
        IdentitySnapshot? seenSnapshot = null;
        Movie? seenCandidate = null;
        var policy = new RelinkPolicy(Validate: (s, c) =>
        {
            seenSnapshot = s;
            seenCandidate = (Movie)c;
            return false;
        });
        var snapshot = Snapshot(60);
        var candidate = Movie(60);

        Assert.False(policy.Accepts(snapshot, candidate));
        Assert.Same(snapshot, seenSnapshot);
        Assert.Same(candidate, seenCandidate);
    }

    private static IdentitySnapshot Snapshot(int seconds) => new() { Kind = "Movie", Name = "Edition Movie", RunTimeTicks = TimeSpan.FromSeconds(seconds).Ticks };

    private static Movie Movie(int seconds) => new() { Id = Guid.NewGuid(), Name = "Edition Movie", RunTimeTicks = TimeSpan.FromSeconds(seconds).Ticks };
}
