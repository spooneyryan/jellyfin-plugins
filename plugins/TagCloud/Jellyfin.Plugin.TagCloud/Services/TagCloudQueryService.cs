using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.TagCloud.Api.Models;
using Jellyfin.Plugin.TagCloud.Configuration;
using Jellyfin.Plugin.TagCloud.Index;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.TagCloud.Services;

/// <summary>
/// Counts the index over one user's titles.
/// </summary>
/// <remarks>
/// <para>The user's titles come from the kit's <see cref="LibraryAccess.CreateQuery"/> (<c>new InternalItemsQuery(user)</c>
/// with the libraries asked for, intersected with the user's) and core's <c>GetItemList</c>, which applies library access,
/// parental ratings and blocked tags in SQL and each item's own <c>IsVisible(user)</c> after (the kit's
/// <c>LibraryAccess</c> remarks). The cheaper <c>GetItemIds</c> has no such step, so this takes
/// the list: no shortcut decides what a user sees.</para>
/// <para>Each user's titles are kept as a mask over the snapshot's ordinals, per scope, the libraries as they resolve for
/// the user at the request (a library switched off server-wide or no longer open to the user changes them), generation
/// and the user's access as it decides what they see (<see cref="AccessKey"/>; 64 masks, the least recently used dropped first),
/// so changing the source, the word count or the narrowing reads nothing from the library, and an admin's change to the
/// user's libraries, parental limit or tags applies at the next request. Jellyfin 10.11 raises no event for such a
/// change.</para>
/// </remarks>
internal sealed class TagCloudQueryService : ITagCloudQuery
{
    /// <summary>How many masks are kept.</summary>
    public const int MaskCacheSize = 64;

    /// <summary>The most narrowing words one request applies.</summary>
    public const int MaxNarrowKeys = 10;

    /// <summary>The most titles one page of <see cref="GetTitles"/> holds.</summary>
    public const int MaxTitlesPerPage = 200;

    private readonly TagCloudIndex _index;
    private readonly LibraryAccess _access;
    private readonly ILibraryManager _library;
    private readonly IPluginConfigurationProvider _configuration;
    private readonly ConcurrentDictionary<string, CachedMask> _masks = new(StringComparer.Ordinal);
    private long _uses;

    /// <summary>
    /// Initializes a new instance of the <see cref="TagCloudQueryService"/> class.
    /// </summary>
    /// <param name="index">The index.</param>
    /// <param name="access">The kit's library access.</param>
    /// <param name="library">The library manager.</param>
    /// <param name="configuration">The settings.</param>
    public TagCloudQueryService(TagCloudIndex index, LibraryAccess access, ILibraryManager library, IPluginConfigurationProvider configuration)
    {
        _index = index;
        _access = access;
        _library = library;
        _configuration = configuration;
    }

    /// <inheritdoc />
    public TagCloudCapabilitiesDto GetCapabilities(bool isAdmin)
    {
        var configuration = _configuration.GetConfiguration();
        return new TagCloudCapabilitiesDto
        {
            Sources = TagCloudIndex.EnabledSources(configuration),
            MaxWordsLimit = WordsLimit(configuration),
            WordStep = PluginConfiguration.WordStep,
            TvMaxWordsDefault = Math.Clamp(configuration.TvMaxWordsDefault, PluginConfiguration.WordStep, WordsLimit(configuration)),
            AllowSharedClouds = configuration.AllowSharedClouds,
            IsAdmin = isAdmin
        };
    }

    /// <inheritdoc />
    public IReadOnlyList<TagCloudLibraryDto> GetLibraries(User user, TagCloudScope scope)
    {
        ArgumentNullException.ThrowIfNull(user);
        TagCloudPageGate.Ensure(_configuration.GetConfiguration());
        var snapshot = RequireSnapshot();
        return _access.VisibleLibraries(user)
            .Select(library => new TagCloudLibraryDto { Id = library.Id, Name = library.Name ?? string.Empty, TitleCount = Mask(user, scope, [library.Id], snapshot).Count })
            .Where(library => library.TitleCount > 0)
            .OrderBy(library => library.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(library => library.Id)
            .ToList();
    }

    /// <inheritdoc />
    public TagCloudWordsDto GetWords(User user, TagCloudSource source, TagCloudScope scope, IReadOnlyList<Guid>? libraryIds, IReadOnlyList<string>? narrow, int? limit)
    {
        ArgumentNullException.ThrowIfNull(user);
        var configuration = _configuration.GetConfiguration();
        var words = RequireWords(source, configuration, out var snapshot);
        var (titles, applied) = Narrowed(Mask(user, scope, libraryIds, snapshot).Titles, words, narrow);
        var counted = words.Values
            .Select(word => (Word: word, Count: CountIn(word, titles)))
            .Where(w => w.Count > 0)
            .OrderByDescending(w => w.Count)
            .ThenBy(w => w.Word.Text, StringComparer.OrdinalIgnoreCase)
            .ThenBy(w => w.Word.Key, StringComparer.Ordinal)
            .ToList();
        var take = Math.Clamp(limit ?? 100, PluginConfiguration.WordStep, WordsLimit(configuration));
        return new TagCloudWordsDto
        {
            Words = counted.Take(take).Select(w => new TagCloudWordDto { Key = w.Word.Key, Text = w.Word.Text, Count = w.Count }).ToList(),
            WordCount = counted.Count,
            TitleCount = titles.Count(t => t),
            Narrow = applied,
            Generation = snapshot.Generation,
            IndexedUtc = snapshot.BuiltUtc
        };
    }

    /// <inheritdoc />
    public TagCloudTitlesDto GetTitles(User user, TagCloudSource source, TagCloudScope scope, IReadOnlyList<Guid>? libraryIds, IReadOnlyList<string>? narrow, string key, TagCloudTitleOrder order, int seed, int startIndex, int limit)
    {
        ArgumentNullException.ThrowIfNull(user);
        var words = RequireWords(source, _configuration.GetConfiguration(), out var snapshot);
        var wordKey = IndexState.KeyOf(key);
        if (wordKey is null || !words.TryGetValue(wordKey, out var word))
        {
            // A word the user's titles don't carry (or none) has no titles: an empty page, never a 404 that would tell
            // a word of hidden titles from an unknown one.
            return new TagCloudTitlesDto();
        }

        var (titles, _) = Narrowed(Mask(user, scope, libraryIds, snapshot).Titles, words, narrow);
        var carrying = word.Ordinals.Where(o => o < titles.Length && titles[o]).Select(o => snapshot.Titles[o]).OfType<TitleRecord>();
        var ordered = order == TagCloudTitleOrder.Random
            ? carrying.OrderBy(t => Shuffle(seed, t.Id)).ThenBy(t => t.Id)
            : carrying.OrderBy(t => t.SortName, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Year).ThenBy(t => t.Id);
        var all = ordered.ToList();
        return new TagCloudTitlesDto
        {
            Total = all.Count,
            Items = all.Skip(Math.Max(0, startIndex)).Take(Math.Clamp(limit, 1, MaxTitlesPerPage))
                .Select(t => new TagCloudTitleDto { Id = t.Id, Name = t.Name, Year = t.Year, Kind = t.Kind.ToString() })
                .ToList()
        };
    }

    private static int WordsLimit(PluginConfiguration configuration)
    {
        var limit = Math.Clamp(configuration.MaxWordsLimit, PluginConfiguration.LowestWordsLimit, PluginConfiguration.HighestWordsLimit);
        return limit - (limit % PluginConfiguration.WordStep);
    }

    private static int CountIn(IndexedWord word, bool[] titles)
    {
        var count = 0;
        foreach (var ordinal in word.Ordinals)
        {
            if (ordinal < titles.Length && titles[ordinal])
            {
                count++;
            }
        }

        return count;
    }

    // The titles carrying every narrowing word the user's titles have; keys none of them carry are dropped, so a word of
    // hidden titles can't be tested for.
    private static (bool[] Titles, IReadOnlyList<string> Applied) Narrowed(bool[] visible, IReadOnlyDictionary<string, IndexedWord> words, IReadOnlyList<string>? narrow)
    {
        var applied = new List<string>();
        var titles = visible;
        foreach (var key in (narrow ?? []).Select(IndexState.KeyOf).OfType<string>().Distinct(StringComparer.Ordinal).Take(MaxNarrowKeys))
        {
            if (!words.TryGetValue(key, out var word) || CountIn(word, visible) == 0)
            {
                continue;
            }

            var next = new bool[titles.Length];
            foreach (var ordinal in word.Ordinals)
            {
                if (ordinal < titles.Length && titles[ordinal])
                {
                    next[ordinal] = true;
                }
            }

            titles = next;
            applied.Add(key);
        }

        return (titles, applied);
    }

    // FNV-1a over the seed and the id's bytes: the same order for the same word every time.
    private static uint Shuffle(int seed, Guid id)
    {
        var hash = 2166136261u ^ (uint)seed;
        foreach (var b in id.ToByteArray())
        {
            hash = (hash ^ b) * 16777619u;
        }

        return hash;
    }

    private TagCloudIndexSnapshot RequireSnapshot()
        => _index.Current ?? throw PluginException.Unavailable(string.Format(
            CultureInfo.InvariantCulture,
            "The tag cloud is being counted ({0:N0} of {1:N0} titles read). Try again in a moment.",
            _index.Done,
            _index.Total));

    private IReadOnlyDictionary<string, IndexedWord> RequireWords(TagCloudSource source, PluginConfiguration configuration, out TagCloudIndexSnapshot snapshot)
    {
        TagCloudPageGate.Ensure(configuration);
        if (!TagCloudIndex.EnabledSources(configuration).Contains(source))
        {
            throw PluginException.FeatureDisabled("This cloud is turned off by your server admin.");
        }

        snapshot = RequireSnapshot();
        if (!snapshot.Words.TryGetValue(source, out var words))
        {
            // A source turned on since the last build: count it now.
            _index.RequestRebuild();
            throw PluginException.Unavailable("This cloud is being counted for the first time. Try again in a moment.");
        }

        return words;
    }

    private (bool[] Titles, int Count) Mask(User user, TagCloudScope scope, IReadOnlyList<Guid>? libraryIds, TagCloudIndexSnapshot snapshot)
    {
        // The key holds the libraries as they resolve now, not as asked: a library switched off server-wide, or one the
        // user may no longer open, changes it at the next request.
        var libraries = _access.ResolveLibraries(libraryIds, user);
        var topParents = LibraryAccess.TopParentIds(libraries).Order().ToArray();
        var key = string.Join('|', user.Id.ToString("N", CultureInfo.InvariantCulture), AccessKey(user), scope, snapshot.Generation, string.Join(',', topParents));
        var use = System.Threading.Interlocked.Increment(ref _uses);
        if (_masks.TryGetValue(key, out var cached))
        {
            cached.LastUse = use;
            return (cached.Titles, cached.Count);
        }

        var titles = new bool[snapshot.Titles.Count];
        var count = 0;
        var query = topParents.Length == 0 ? null : _access.CreateQuery(user, libraries.Select(l => l.Id));
        if (query is not null)
        {
            query.IncludeItemTypes = TitleReader.KindsOf(scope);
            query.Recursive = true;
            query.IsVirtualItem = false;
            query.DtoOptions = TitleReader.NoImagesOrUserData();
            foreach (var item in _library.GetItemList(query))
            {
                if (!LibraryAccess.IsOwned(item) && snapshot.Ordinals.TryGetValue(item.Id, out var ordinal) && !titles[ordinal])
                {
                    titles[ordinal] = true;
                    count++;
                }
            }
        }

        _masks[key] = new CachedMask(titles, count) { LastUse = use };
        if (_masks.Count > MaskCacheSize)
        {
            foreach (var old in _masks.OrderBy(m => m.Value.LastUse).Take(_masks.Count - MaskCacheSize).ToList())
            {
                _masks.TryRemove(old.Key, out _);
            }
        }

        return (titles, count);
    }

    /// <summary>
    /// Gets a digest of what in a user's policy decides which titles they see: all libraries or which ones, the blocked
    /// libraries, the parental limit, the blocked unrated kinds, and the blocked and allowed tags. The values are hashed
    /// as stored, untrimmed and with blank ones kept, as core reads them; only their order is ignored.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <returns>16 hex digits of SHA-256 over them.</returns>
    internal static string AccessKey(User user)
    {
        static string List(User user, PreferenceKind kind)
            => string.Join('\u001f', user.GetPreference(kind).Order(StringComparer.Ordinal));

        var text = string.Join(
            '\n',
            user.HasPermission(PermissionKind.EnableAllFolders) ? "all" : "some",
            List(user, PreferenceKind.EnabledFolders),
            List(user, PreferenceKind.BlockedMediaFolders),
            user.MaxParentalRatingScore?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            user.MaxParentalRatingSubScore?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            List(user, PreferenceKind.BlockUnratedItems),
            List(user, PreferenceKind.BlockedTags),
            List(user, PreferenceKind.AllowedTags));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0, 8);
    }

    private sealed class CachedMask(bool[] titles, int count)
    {
        public bool[] Titles { get; } = titles;

        public int Count { get; } = count;

        public long LastUse { get; set; }
    }
}
