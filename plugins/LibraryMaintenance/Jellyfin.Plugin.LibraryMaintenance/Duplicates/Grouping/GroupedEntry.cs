using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.LibraryMaintenance.Duplicates.Index;
using Jellyfin.Plugin.LibraryMaintenance.Duplicates.Normalization;

namespace Jellyfin.Plugin.LibraryMaintenance.Duplicates.Grouping;

/// <summary>
/// One index entry as the grouper sees it for a caller: the in-scope view of the version
/// set (<see cref="EntryScope"/>), its title and year, its ids and its bytes, all of the in-scope members
/// only. The projection over every library the index read is kept per snapshot and title rules, and narrowed
/// to a caller's libraries with <see cref="Narrow"/>.
/// </summary>
internal sealed record GroupedEntry
{
    /// <summary>Gets the index's entry.</summary>
    public required VersionEntry Entry { get; init; }

    /// <summary>Gets the in-scope representative: the root when it is in scope, else the in-scope member with the lowest id.</summary>
    public required MovieRecord Representative { get; init; }

    /// <summary>Gets the in-scope members, the representative first.</summary>
    public required IReadOnlyList<MovieRecord> Members { get; init; }

    /// <summary>Gets the in-scope members' libraries within the scope.</summary>
    public required IReadOnlyList<Guid> LibraryIds { get; init; }

    /// <summary>Gets the sources the caller can see (<see cref="EntryView.SourceCount"/>).</summary>
    public required int SourceCount { get; init; }

    /// <summary>Gets the broken states the in-scope members show.</summary>
    public required VersionSetState State { get; init; }

    /// <summary>Gets a value indicating whether every member of the set is in scope.</summary>
    public required bool IsWhole { get; init; }

    /// <summary>Gets the representative's normalised name.</summary>
    public required NormalizedTitle Title { get; init; }

    /// <summary>Gets the representative's year: <c>ProductionYear ?? PremiereYear ?? the title's year</c>.</summary>
    public required TitleYear Year { get; init; }

    /// <summary>Gets the set's TMDb id: the representative's, else the first in-scope member's that has one.</summary>
    public string? Tmdb { get; init; }

    /// <summary>Gets the set's IMDb id, likewise.</summary>
    public string? Imdb { get; init; }

    /// <summary>Gets the bytes of the in-scope members whose size is known.</summary>
    public long KnownBytes { get; init; }

    /// <summary>
    /// Gets a value indicating whether the in-scope members hold bytes the index doesn't know: a member of unknown size,
    /// local alternates or stacked parts (the page shows "≥"). A member out of scope never makes it so.
    /// </summary>
    public bool HasUnknownBytes { get; init; }

    /// <summary>Gets the representative's id, the entry's id in a group.</summary>
    public Guid Id => Representative.Id;

    /// <summary>Gets a value indicating whether the set has a TMDb or an IMDb id.</summary>
    public bool HasProviderId => Tmdb is not null || Imdb is not null;

    /// <summary>
    /// Takes an entry over every library the index read (only a member outside the index is out of scope).
    /// </summary>
    /// <param name="entry">The version set.</param>
    /// <param name="options">The title rules.</param>
    /// <returns>The grouper's entry; its title key is empty when the name is (such an entry never groups).</returns>
    public static GroupedEntry From(VersionEntry entry, NormalizationOptions options) => From(entry, options, null)!;

    /// <summary>
    /// Takes the caller's view of an entry.
    /// </summary>
    /// <param name="entry">The version set.</param>
    /// <param name="options">The title rules.</param>
    /// <param name="scope">The caller's libraries, or null for every library the index read.</param>
    /// <param name="hidden">The items the caller can't see, or null for none.</param>
    /// <returns>The grouper's entry, or null when no member is in scope.</returns>
    public static GroupedEntry? From(VersionEntry entry, NormalizationOptions options, IReadOnlySet<Guid>? scope, IReadOnlySet<Guid>? hidden = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(options);
        if (EntryScope.View(entry, scope, hidden) is not { } view)
        {
            return null;
        }

        var representative = view.Representative;
        var tmdb = ProviderId(representative.Tmdb) ?? view.Members.Select(m => ProviderId(m.Tmdb)).FirstOrDefault(i => i is not null);
        var imdb = ProviderId(representative.Imdb) ?? view.Members.Select(m => ProviderId(m.Imdb)).FirstOrDefault(i => i is not null);
        var title = TitleNormalizer.Normalize(representative.Name, options, IsReleaseName(representative, tmdb, imdb));
        return new GroupedEntry
        {
            Entry = entry,
            Representative = representative,
            Members = view.Members,
            LibraryIds = view.LibraryIds,
            SourceCount = view.SourceCount,
            State = view.State,
            IsWhole = view.IsWhole,
            Title = title,
            Year = TitleYear.Resolve(representative.ProductionYear, representative.PremiereYear, title.BracketYear),
            Tmdb = tmdb,
            Imdb = imdb,
            KnownBytes = view.Members.Sum(m => m.Size is > 0 ? m.Size.Value : 0),
            HasUnknownBytes = view.Members.Any(m => m.Size is not > 0 || m.LocalAlternateCount > 0 || m.PartCount > 1)
        };
    }

    /// <summary>
    /// Narrows a projection over every library (<see cref="From(VersionEntry, NormalizationOptions)"/>) to a caller's
    /// libraries and the items they can see: the same projection when every library of its members is in scope and none is
    /// hidden, the same with fewer libraries when every member is in scope, else the in-scope members' own projection (their representative, title, ids and bytes).
    /// </summary>
    /// <param name="full">The projection over every library.</param>
    /// <param name="scope">The caller's libraries, or null for every one.</param>
    /// <param name="options">The title rules the projection was made with.</param>
    /// <param name="hidden">The items the caller can't see, or null for none.</param>
    /// <returns>The caller's entry, or null when no member is in scope.</returns>
    public static GroupedEntry? Narrow(GroupedEntry full, IReadOnlySet<Guid>? scope, NormalizationOptions options, IReadOnlySet<Guid>? hidden = null)
    {
        ArgumentNullException.ThrowIfNull(full);
        if (scope is null && hidden is null)
        {
            return full;
        }

        var all = true;
        var any = false;
        foreach (var member in full.Members)
        {
            if (EntryScope.Contains(member, scope, hidden))
            {
                any = true;
            }
            else
            {
                all = false;
            }
        }

        if (!any)
        {
            return null;
        }

        if (all)
        {
            var libraries = EntryScope.Libraries(full.LibraryIds, scope);
            return ReferenceEquals(libraries, full.LibraryIds) ? full : full with { LibraryIds = libraries };
        }

        return From(full.Entry, options, scope, hidden);
    }

    // Rule N10: an item with no TMDb or IMDb id whose name is its file or folder name.
    private static bool IsReleaseName(MovieRecord record, string? tmdb, string? imdb)
    {
        if (tmdb is not null || imdb is not null || string.IsNullOrEmpty(record.Path))
        {
            return false;
        }

        var file = System.IO.Path.GetFileNameWithoutExtension(record.Path);
        var folder = string.IsNullOrEmpty(record.ContainingFolder) ? null : System.IO.Path.GetFileName(record.ContainingFolder);
        return string.Equals(record.Name, file, StringComparison.OrdinalIgnoreCase)
            || string.Equals(record.Name, folder, StringComparison.OrdinalIgnoreCase);
    }

    // Ids compare trimmed and in lower case (IMDb's "tt" prefix).
    private static string? ProviderId(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
}
