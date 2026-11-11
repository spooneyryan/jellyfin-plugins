using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.LibraryMaintenance.Duplicates.Index;
using Jellyfin.Plugin.LibraryMaintenance.Duplicates.Normalization;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.LibraryMaintenance.Duplicates.Grouping;

/// <summary>
/// Groups the index's entries into duplicate groups: a pure function of the entries,
/// the options and the caller's libraries, in O(n) with dictionary buckets. Each entry is the caller's view of its set
/// (<see cref="GroupedEntry"/>): nothing of a member outside the caller's libraries shows.
/// </summary>
/// <remarks>
/// <para><b>Title only</b> groups the entries of a title key; <b>Title + Year</b> those of a title key and a year. An entry
/// with no year groups, under <see cref="UnknownYearPolicy.MatchUnknownOnly"/>, only with the title's other entries with
/// no year; under <see cref="UnknownYearPolicy.MatchAny"/> it joins the title's entries of a year when they all have the
/// same year, and with several years it groups only with the other entries with no year, flagged
/// <see cref="GroupFlags.Ambiguous"/>. A group has at least two entries; a version set is one entry.</para>
/// <para><b>Confidence</b>, the first rule that matches: <see cref="GroupConfidence.Conflict"/> when two entries carry
/// different TMDb ids, or different IMDb ids; <see cref="GroupConfidence.Possible"/> when known years differ, when two
/// entries of the same edition have runtimes differing by more than 15 % of the longer and more than 10 minutes, or when
/// an unknown-year match formed the group; <see cref="GroupConfidence.Confirmed"/> when at least two entries share an id
/// (every id then is the same); <see cref="GroupConfidence.Likely"/> for the same key and the same known year;
/// <see cref="GroupConfidence.Possible"/> otherwise. <see cref="GroupFlags.Ambiguous"/> caps nothing: two unknown-year
/// entries that share an id are Confirmed, and the flag says the year they belong to is unclear.</para>
/// <para><b>The suggested keep</b>, advisory only: a real file (not a
/// <c>.strm</c> shortcut or a placeholder), then a video file over a disc rip, the higher width, ids, the higher bitrate,
/// the larger size, more sources, the older date added, each on the entry's representative (the sources are the set's in
/// scope); the lower id breaks a tie. <b>Reclaimable</b>: the bytes of every other entry's in-scope members.</para>
/// </remarks>
internal static class DuplicateGrouper
{
    /// <summary>How much of the longer runtime they must differ by, more than, to cap a group.</summary>
    public const double RuntimeShare = 0.15;

    /// <summary>How long the runtimes must differ by, more than, to cap a group.</summary>
    public static readonly TimeSpan RuntimeDifference = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Groups the entries.
    /// </summary>
    /// <param name="entries">The index's entries.</param>
    /// <param name="options">The options.</param>
    /// <param name="libraryIds">The libraries in scope (an entry is in scope when any of its members is in one, and shows
    /// only its members in scope), or null for every library.</param>
    /// <returns>The groups and what they were made from.</returns>
    public static GroupingResult Group(IEnumerable<VersionEntry> entries, GroupingOptions options, IReadOnlySet<Guid>? libraryIds = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(options);
        return Group(BucketEntries(entries.Select(e => GroupedEntry.From(e, options.Normalization, libraryIds))), options);
    }

    /// <summary>
    /// Projects the entries over every library the index read (kept per snapshot and title rules).
    /// </summary>
    /// <param name="entries">The index's entries.</param>
    /// <param name="options">The title rules.</param>
    /// <returns>One projection per entry, in the entries' order.</returns>
    public static IReadOnlyList<GroupedEntry> Project(IReadOnlyList<VersionEntry> entries, NormalizationOptions options)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(options);
        var projected = new GroupedEntry[entries.Count];
        for (var i = 0; i < projected.Length; i++)
        {
            projected[i] = GroupedEntry.From(entries[i], options);
        }

        return projected;
    }

    /// <summary>
    /// Narrows projections to the caller's libraries and the items they can see (<see cref="GroupedEntry.Narrow"/>) and
    /// buckets them by title key.
    /// </summary>
    /// <param name="projected">The projections over every library (<see cref="Project"/>).</param>
    /// <param name="libraryIds">The libraries in scope, or null for every library.</param>
    /// <param name="options">The title rules the projections were made with.</param>
    /// <param name="hidden">The items the caller can't see, or null for none.</param>
    /// <returns>The buckets.</returns>
    public static TitleBuckets Bucket(IReadOnlyList<GroupedEntry> projected, IReadOnlySet<Guid>? libraryIds, NormalizationOptions options, IReadOnlySet<Guid>? hidden = null)
    {
        ArgumentNullException.ThrowIfNull(projected);
        ArgumentNullException.ThrowIfNull(options);
        return BucketEntries(projected.Select(p => GroupedEntry.Narrow(p, libraryIds, options, hidden)));
    }

    /// <summary>
    /// Groups bucketed entries.
    /// </summary>
    /// <param name="buckets">The caller's entries by title key (<see cref="Bucket"/>).</param>
    /// <param name="options">The options (the mode and the unknown-year policy; the title rules are the buckets').</param>
    /// <returns>The groups and what they were made from.</returns>
    public static GroupingResult Group(TitleBuckets buckets, GroupingOptions options)
    {
        ArgumentNullException.ThrowIfNull(buckets);
        ArgumentNullException.ThrowIfNull(options);
        var groups = Candidates(buckets, options).Select(Build).ToList();
        groups.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key) is var byKey and not 0 ? byKey : string.CompareOrdinal(a.Id, b.Id));
        return new GroupingResult(groups, buckets.Entries, buckets.Movies, buckets.NoTitle);
    }

    /// <summary>
    /// Counts the groups <see cref="Group(TitleBuckets, GroupingOptions)"/> would make, without making them: the other
    /// mode's count for <c>AlternateModeGroupCount</c>, one pass over the buckets.
    /// </summary>
    /// <param name="buckets">The caller's entries by title key.</param>
    /// <param name="options">The options.</param>
    /// <returns>The number of groups.</returns>
    public static int Count(TitleBuckets buckets, GroupingOptions options)
    {
        ArgumentNullException.ThrowIfNull(buckets);
        ArgumentNullException.ThrowIfNull(options);
        return Candidates(buckets, options).Count();
    }

    /// <summary>
    /// Gets a group's id: the first 16 hex digits, in lower case, of SHA-256 over the mode's name, <c>:</c>, and
    /// the representatives' ids (32 hex digits each, in ordinal order) joined with <c>,</c>. It changes when the
    /// membership changes, which delete uses to see that a group changed.
    /// </summary>
    /// <param name="mode">The mode.</param>
    /// <param name="representativeIds">The entries' representative ids.</param>
    /// <returns>The id.</returns>
    public static string GroupId(MatchMode mode, IEnumerable<Guid> representativeIds)
    {
        ArgumentNullException.ThrowIfNull(representativeIds);
        var ids = representativeIds.Select(id => id.ToString("N", CultureInfo.InvariantCulture)).Order(StringComparer.Ordinal);
        var text = mode.ToString() + ":" + string.Join(',', ids);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0, 8).ToLowerInvariant();
    }

    /// <summary>
    /// Orders the entries as the suggested keep does, the best first:
    /// a real file (not a shortcut, a placeholder or a 0-byte file), a video file over a disc rip or an ISO,
    /// the higher width, ids (Identify can add them later; a deleted 4K file can't come back), the higher bitrate, the
    /// larger size, more sources, the older date added, the lower item id.
    /// </summary>
    /// <param name="a">One entry.</param>
    /// <param name="b">The other.</param>
    /// <returns>Less than zero when <paramref name="a"/> is the better keep.</returns>
    public static int CompareKeep(GroupedEntry a, GroupedEntry b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        var x = a.Representative;
        var y = b.Representative;
        int c;
        if ((c = IsRealFile(y).CompareTo(IsRealFile(x))) != 0
            || (c = (y.VideoType == VideoType.VideoFile).CompareTo(x.VideoType == VideoType.VideoFile)) != 0
            || (c = (y.Width ?? 0).CompareTo(x.Width ?? 0)) != 0
            || (c = b.HasProviderId.CompareTo(a.HasProviderId)) != 0
            || (c = (y.TotalBitrate ?? 0).CompareTo(x.TotalBitrate ?? 0)) != 0
            || (c = (y.Size ?? 0).CompareTo(x.Size ?? 0)) != 0
            || (c = b.SourceCount.CompareTo(a.SourceCount)) != 0
            || (c = x.DateCreated.CompareTo(y.DateCreated)) != 0)
        {
            return c;
        }

        return x.Id.CompareTo(y.Id);
    }

    // A file the server holds the movie in: not a .strm shortcut, not a disc placeholder, and not 0 bytes (a truncated or
    // failed copy). An unknown size counts as real: Jellyfin leaves it unset on a disc folder.
    private static bool IsRealFile(MovieRecord record) => !record.IsShortcut && !record.IsPlaceHolder && record.Size != 0;

    private static TitleBuckets BucketEntries(IEnumerable<GroupedEntry?> entries)
    {
        var buckets = new Dictionary<string, List<GroupedEntry>>(StringComparer.Ordinal);
        int inScope = 0, movies = 0, noTitle = 0;
        foreach (var grouped in entries)
        {
            if (grouped is null)
            {
                continue;
            }

            if (grouped.Title.Key.Length == 0)
            {
                noTitle++;
                continue;
            }

            inScope++;
            movies += grouped.Members.Count;
            if (!buckets.TryGetValue(grouped.Title.Key, out var bucket))
            {
                buckets[grouped.Title.Key] = bucket = [];
            }

            bucket.Add(grouped);
        }

        var candidates = buckets.Where(b => b.Value.Count >= 2).Select(b => new TitleBucket(b.Key, b.Value)).ToList();
        return new TitleBuckets(candidates, inScope, movies, noTitle);
    }

    // The member lists the mode makes of the buckets with two or more entries, before Build: shared by Group and Count.
    private static IEnumerable<Candidate> Candidates(TitleBuckets buckets, GroupingOptions options)
    {
        foreach (var bucket in buckets.Candidates)
        {
            if (options.Mode == MatchMode.Title)
            {
                yield return new Candidate(MatchMode.Title, bucket.Key, bucket.Entries, GroupReasons.Title, GroupFlags.None);
                continue;
            }

            foreach (var candidate in ByYear(bucket.Key, bucket.Entries, options.UnknownYear))
            {
                yield return candidate;
            }
        }
    }

    // Title + Year: one group per known year with two or more entries; the entries with no year by the policy.
    private static IEnumerable<Candidate> ByYear(string titleKey, IReadOnlyList<GroupedEntry> bucket, UnknownYearPolicy policy)
    {
        var unknown = bucket.Where(e => e.Year.Year is null).ToList();
        var years = bucket.Where(e => e.Year.Year is not null).GroupBy(e => e.Year.Year!.Value).OrderBy(g => g.Key).ToList();
        var unknownJoined = false;
        foreach (var year in years)
        {
            var members = year.ToList();
            var reasons = GroupReasons.TitleAndYear;
            if (policy == UnknownYearPolicy.MatchAny && years.Count == 1 && unknown.Count > 0)
            {
                members.AddRange(unknown);
                reasons |= GroupReasons.UnknownYearMatch;
                unknownJoined = true;
            }

            if (members.Count >= 2)
            {
                yield return new Candidate(MatchMode.TitleAndYear, members[0].Year.KeyFor(titleKey), members, reasons, GroupFlags.None);
            }
        }

        if (!unknownJoined && unknown.Count >= 2)
        {
            var flags = policy == UnknownYearPolicy.MatchAny && years.Count > 1 ? GroupFlags.Ambiguous : GroupFlags.None;
            yield return new Candidate(MatchMode.TitleAndYear, TitleYear.Unknown.KeyFor(titleKey), unknown, GroupReasons.TitleAndYear, flags);
        }
    }

    private static DuplicateGroup Build(Candidate candidate)
    {
        var reasons = candidate.Reasons;
        var flags = candidate.Flags;
        var entries = candidate.Members.ToList();
        entries.Sort(CompareKeep);
        var keep = entries[0];

        var tmdbs = entries.Where(e => e.Tmdb is not null).GroupBy(e => e.Tmdb, StringComparer.Ordinal).ToList();
        var imdbs = entries.Where(e => e.Imdb is not null).GroupBy(e => e.Imdb, StringComparer.Ordinal).ToList();
        var conflict = tmdbs.Count > 1 || imdbs.Count > 1;
        var sharedTmdb = tmdbs.Count == 1 && tmdbs[0].Count() >= 2;
        var sharedImdb = imdbs.Count == 1 && imdbs[0].Count() >= 2;
        if (sharedTmdb)
        {
            reasons |= GroupReasons.SameTmdbId;
        }

        if (sharedImdb)
        {
            reasons |= GroupReasons.SameImdbId;
        }

        var years = entries.Where(e => e.Year.Year is not null).Select(e => e.Year.Year!.Value).Distinct().Order().ToList();
        var yearsDiffer = years.Count > 1;
        var anyUnknownYear = entries.Any(e => e.Year.Year is null);
        var editionsDiffer = entries.Select(e => e.Title.Edition).Distinct(StringComparer.Ordinal).Count() > 1;
        var runtimesDiffer = RuntimesDiffer(entries);

        flags |= Flag(yearsDiffer, GroupFlags.YearsDiffer)
            | Flag(anyUnknownYear, GroupFlags.YearUnknown)
            | Flag(conflict, GroupFlags.DifferentProviderIds)
            | Flag(!conflict && (sharedTmdb || sharedImdb) && yearsDiffer, GroupFlags.SameIdYearsDiffer)
            | Flag(runtimesDiffer, GroupFlags.RuntimesDiffer)
            | Flag(editionsDiffer, GroupFlags.EditionsDiffer)
            | Flag(CrossLibrary(entries), GroupFlags.CrossLibrary)
            | Flag(entries.Any(e => e.State != Kit.Library.VersionSetState.None), GroupFlags.BrokenMerge)
            | Flag(entries.Any(e => e.Members.Any(m => m.VideoType != VideoType.VideoFile)), GroupFlags.ContainsDiscRip)
            | Flag(entries.Any(e => e.Members.Any(m => m.IsShortcut)), GroupFlags.ContainsShortcut)
            | Flag(entries.Any(e => e.Members.Any(m => m.IsPlaceHolder)), GroupFlags.ContainsPlaceholder);

        GroupConfidence confidence;
        if (conflict)
        {
            confidence = GroupConfidence.Conflict;
        }
        else if (yearsDiffer || runtimesDiffer || reasons.HasFlag(GroupReasons.UnknownYearMatch))
        {
            confidence = GroupConfidence.Possible;
        }
        else if (sharedTmdb || sharedImdb)
        {
            confidence = GroupConfidence.Confirmed;
        }
        else if (!anyUnknownYear && years.Count == 1)
        {
            confidence = GroupConfidence.Likely;
        }
        else
        {
            confidence = GroupConfidence.Possible;
        }

        var others = entries.Skip(1).ToList();
        return new DuplicateGroup(
            GroupId(candidate.Mode, entries.Select(e => e.Id)),
            candidate.Mode,
            candidate.Key,
            keep.Representative.Name,
            entries,
            confidence,
            flags,
            reasons,
            years,
            others.Sum(e => e.KnownBytes),
            others.Any(e => e.HasUnknownBytes));
    }

    // Two entries of the same edition (no edition counts as one) whose known runtimes, on the representatives, differ by
    // more than 10 minutes and by more than 15 % of the longer of the two (an edition exempts a pair,
    // never the group). Within an edition the longest and the shortest are the pair that differs most on both counts.
    private static bool RuntimesDiffer(List<GroupedEntry> entries)
    {
        foreach (var edition in entries.Where(e => e.Representative.RunTimeTicks is > 0).GroupBy(e => e.Title.Edition ?? string.Empty, StringComparer.Ordinal))
        {
            long longest = long.MinValue, shortest = long.MaxValue;
            var count = 0;
            foreach (var entry in edition)
            {
                var ticks = entry.Representative.RunTimeTicks!.Value;
                longest = Math.Max(longest, ticks);
                shortest = Math.Min(shortest, ticks);
                count++;
            }

            var difference = longest - shortest;
            if (count >= 2 && difference > RuntimeDifference.Ticks && difference > RuntimeShare * longest)
            {
                return true;
            }
        }

        return false;
    }

    // No one library (of the caller's) holds every entry.
    private static bool CrossLibrary(List<GroupedEntry> entries)
    {
        IEnumerable<Guid> common = entries[0].LibraryIds;
        foreach (var entry in entries.Skip(1))
        {
            common = common.Intersect(entry.LibraryIds);
        }

        return !common.Any();
    }

    private static GroupFlags Flag(bool on, GroupFlags flag) => on ? flag : GroupFlags.None;

    private readonly record struct Candidate(MatchMode Mode, string Key, IReadOnlyList<GroupedEntry> Members, GroupReasons Reasons, GroupFlags Flags);
}
