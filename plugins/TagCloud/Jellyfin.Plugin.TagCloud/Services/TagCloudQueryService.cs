using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.TagCloud.Api.Models;
using Jellyfin.Plugin.TagCloud.Configuration;
using Jellyfin.Plugin.TagCloud.Index;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

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
/// <para>A word's titles are filtered and sorted here, over the index: what they sort and filter by (sort name, original
/// title, release date, date added, rating, picture size) is in each <see cref="TitleRecord"/>, columns of the title's
/// row read with it and patched with it, so a page of a filtered list reads nothing but the posters of that page. Only
/// "unwatched" asks the server, per request and only for the titles still in after the other filters: played state is
/// the user's and changes with every play, which no index of titles follows.</para>
/// <para>A person's photo (<see cref="TagCloudWordDto.PersonId"/>, <see cref="TagCloudWordDto.ImageTag"/>) is looked up by
/// name (<c>ILibraryManager.GetPerson</c>, read-only) for the words of a people cloud that a response holds, and kept per
/// index generation (at most <see cref="PersonCacheSize"/> names), so a cloud drawn again looks nobody up.</para>
/// </remarks>
internal sealed class TagCloudQueryService : ITagCloudQuery
{
    /// <summary>How many masks are kept.</summary>
    public const int MaskCacheSize = 64;

    /// <summary>The most narrowing words one request applies.</summary>
    public const int MaxNarrowKeys = 10;

    /// <summary>The most titles one page of <c>GetTitles</c> holds.</summary>
    public const int MaxTitlesPerPage = 200;

    /// <summary>How many people's photos are kept per index generation.</summary>
    public const int PersonCacheSize = 5000;

    private readonly TagCloudIndex _index;
    private readonly LibraryAccess _access;
    private readonly ILibraryManager _library;
    private readonly IPluginConfigurationProvider _configuration;
    private readonly IImageProcessor? _images;
    private readonly IUserDataManager? _userData;
    private readonly ConcurrentDictionary<string, CachedMask> _masks = new(StringComparer.Ordinal);
    private readonly object _peopleLock = new();
    private Dictionary<string, PersonPicture> _people = new(StringComparer.Ordinal);
    private long _peopleGeneration = -1;
    private long _uses;

    /// <summary>
    /// Initializes a new instance of the <see cref="TagCloudQueryService"/> class.
    /// </summary>
    /// <param name="index">The index.</param>
    /// <param name="access">The kit's library access.</param>
    /// <param name="library">The library manager.</param>
    /// <param name="configuration">The settings.</param>
    /// <param name="images">The image processor that gives a title's poster its tag; null leaves the titles without one.</param>
    /// <param name="userData">The user data manager, for the unwatched filter; null makes that filter 503.</param>
    public TagCloudQueryService(TagCloudIndex index, LibraryAccess access, ILibraryManager library, IPluginConfigurationProvider configuration, IImageProcessor? images = null, IUserDataManager? userData = null)
    {
        _images = images;
        _userData = userData;
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
        var people = PeopleReader.IsPeople(source);
        return new TagCloudWordsDto
        {
            Words = counted.Take(take).Select(w =>
            {
                var dto = new TagCloudWordDto { Key = w.Word.Key, Text = w.Word.Text, Count = w.Count };
                if (people && PictureOf(w.Word, snapshot.Generation) is { } picture)
                {
                    dto.PersonId = picture.Id;
                    dto.ImageTag = picture.ImageTag;
                }

                return dto;
            }).ToList(),
            WordCount = counted.Count,
            TitleCount = titles.Count(t => t),
            Narrow = applied,
            Generation = snapshot.Generation,
            IndexedUtc = snapshot.BuiltUtc
        };
    }

    /// <inheritdoc />
    public TagCloudTitlesDto GetTitles(User user, TagCloudSource source, TagCloudScope scope, IReadOnlyList<Guid>? libraryIds, IReadOnlyList<string>? narrow, string key, TagCloudTitleOrder order, int seed, int startIndex, int limit, TagCloudTitleSort? sort = null, bool descending = false, string? query = null, TagCloudMinResolution minResolution = TagCloudMinResolution.Any, bool unwatched = false)
        => GetTitles(user, source, scope, libraryIds, narrow, key, order, seed, startIndex, limit, new TagCloudTitleFilter { Sort = sort, Descending = descending, Query = query, MinResolution = minResolution, Unwatched = unwatched });

    /// <summary>
    /// Gets a page of one word's titles as the user sees them, filtered and sorted before the page is cut.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="source">What the word is.</param>
    /// <param name="scope">Which titles.</param>
    /// <param name="libraryIds">The libraries, or null or empty for every library the user can open.</param>
    /// <param name="narrow">Word keys the titles must also carry, or null.</param>
    /// <param name="key">The word's key.</param>
    /// <param name="order">Name or Random.</param>
    /// <param name="seed">The shuffle's seed.</param>
    /// <param name="startIndex">How many to skip (0 or more).</param>
    /// <param name="limit">The most to return (1–200).</param>
    /// <param name="filter">The filters and the sort, or null for none.</param>
    /// <returns>The page.</returns>
    internal TagCloudTitlesDto GetTitles(User user, TagCloudSource source, TagCloudScope scope, IReadOnlyList<Guid>? libraryIds, IReadOnlyList<string>? narrow, string key, TagCloudTitleOrder order, int seed, int startIndex, int limit, TagCloudTitleFilter? filter)
    {
        ArgumentNullException.ThrowIfNull(user);
        filter ??= new TagCloudTitleFilter();
        Validate(order, startIndex, limit, filter);
        var words = RequireWords(source, _configuration.GetConfiguration(), out var snapshot);
        var wordKey = IndexState.KeyOf(key);
        if (wordKey is null || !words.TryGetValue(wordKey, out var word))
        {
            // A word the user's titles don't carry (or none) has no titles: an empty page, never a 404 that would tell
            // a word of hidden titles from an unknown one.
            return new TagCloudTitlesDto();
        }

        var (titles, _) = Narrowed(Mask(user, scope, libraryIds, snapshot).Titles, words, narrow);
        var carrying = word.Ordinals.Where(o => o < titles.Length && titles[o]).Select(o => snapshot.Titles[o]).OfType<TitleRecord>().ToList();
        var all = Ordered(Filtered(user, carrying, filter), order, seed, filter).ToList();
        return new TagCloudTitlesDto
        {
            Total = all.Count,
            Unfiltered = carrying.Count,
            Items = all.Skip(startIndex).Take(limit)
                .Select(t => new TagCloudTitleDto { Id = t.Id, Name = t.Name, Year = t.Year, Kind = t.Kind.ToString(), ImageTag = PrimaryImageTag(t.Id) })
                .ToList()
        };
    }

    // What a request may ask for; anything else is a 400 that says why, never a silent default.
    private static void Validate(TagCloudTitleOrder order, int startIndex, int limit, TagCloudTitleFilter filter)
    {
        if (startIndex < 0)
        {
            throw PluginException.Invalid("startIndex can't be negative.");
        }

        if (limit < 1 || limit > MaxTitlesPerPage)
        {
            throw PluginException.Invalid(string.Format(CultureInfo.InvariantCulture, "limit must be from 1 to {0}.", MaxTitlesPerPage));
        }

        if (order == TagCloudTitleOrder.Random && (filter.Sort is not null || filter.Descending))
        {
            throw PluginException.Invalid("A shuffle (order=Random) has no sort; ask for one or the other.");
        }

        if (filter.Query is { Length: > TagCloudTitleFilter.MaxQueryLength })
        {
            throw PluginException.Invalid(string.Format(CultureInfo.InvariantCulture, "The name filter takes at most {0} characters.", TagCloudTitleFilter.MaxQueryLength));
        }

        if (!Enum.IsDefined(filter.MinResolution) || (filter.Sort is { } sort && !Enum.IsDefined(sort)))
        {
            throw PluginException.Invalid("Unknown sort or minimum resolution.");
        }
    }

    private static IEnumerable<TitleRecord> Ordered(IEnumerable<TitleRecord> titles, TagCloudTitleOrder order, int seed, TagCloudTitleFilter filter)
    {
        if (order == TagCloudTitleOrder.Random && filter.Sort is null)
        {
            return titles.OrderBy(t => Shuffle(seed, t.Id)).ThenBy(t => t.Id);
        }

        var down = filter.Descending;
        var sorted = (filter.Sort ?? TagCloudTitleSort.Name) switch
        {
            // A title without the value goes last both ways round.
            TagCloudTitleSort.ReleaseDate => ByValue(titles, t => t.ReleaseDate, down),
            TagCloudTitleSort.DateAdded => ByValue(titles, t => t.DateCreated, down),
            TagCloudTitleSort.Rating => ByValue(titles, t => t.CommunityRating, down),
            _ => down ? titles.OrderByDescending(t => t.SortName, StringComparer.OrdinalIgnoreCase) : titles.OrderBy(t => t.SortName, StringComparer.OrdinalIgnoreCase)
        };
        return sorted.ThenBy(t => t.SortName, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Year).ThenBy(t => t.Id);
    }

    private static IOrderedEnumerable<TitleRecord> ByValue<T>(IEnumerable<TitleRecord> titles, Func<TitleRecord, T?> value, bool descending)
        where T : struct
    {
        var known = titles.OrderBy(t => value(t) is null ? 1 : 0);
        return descending ? known.ThenByDescending(value) : known.ThenBy(value);
    }

    // The cheap filters first (name, picture size: the index's own columns), then unwatched, which asks the server, on
    // what is left.
    private IEnumerable<TitleRecord> Filtered(User user, IEnumerable<TitleRecord> titles, TagCloudTitleFilter filter)
    {
        var query = TitleText.Fold(filter.Query?.Trim());
        if (query.Length > 0)
        {
            titles = titles.Where(t => TitleText.Contains(t.SearchText ?? TitleText.Fold(t.Name) + "\n" + TitleText.Fold(t.OriginalTitle), query));
        }

        if (filter.MinResolution != TagCloudMinResolution.Any)
        {
            var least = (int)filter.MinResolution;
            titles = titles.Where(t => t.Kind != BaseItemKind.Movie || TitleText.ResolutionStep(t.Width, t.Height) >= least);
        }

        if (filter.Unwatched)
        {
            if (_userData is null)
            {
                throw PluginException.Unavailable("The server's watched state can't be read.");
            }

            titles = titles.Where(t => !IsWatched(user, t)).ToList();
        }

        return titles;
    }

    // Whether the user has watched a title, as the server says it now: a movie played; a series with no episode left
    // unplayed (core's folder rule: UserItemDataDto.Played from the unplayed count). A title gone from the library since
    // the mask was read counts as not watched.
    private bool IsWatched(User user, TitleRecord title)
    {
        try
        {
            var item = _library.GetItemById(title.Id);
            if (item is null)
            {
                return false;
            }

            return item is Series
                ? _userData!.GetUserDataDto(item, null, user, new DtoOptions(false))?.Played == true
                : _userData!.GetUserData(user, item)?.Played == true;
        }
#pragma warning disable CA1031 // One title's played state that can't be read keeps it in the list rather than failing the page.
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }
    }

    // The person a people word names, with their photo's tag: by name, as the index keeps people
    // (ILibraryManager.GetPerson finds, never creates). Kept per index generation; a name with no person is kept as none.
    private PersonPicture? PictureOf(IndexedWord word, long generation)
    {
        lock (_peopleLock)
        {
            if (_peopleGeneration != generation || _people.Count > PersonCacheSize)
            {
                _people = new Dictionary<string, PersonPicture>(StringComparer.Ordinal);
                _peopleGeneration = generation;
            }

            if (_people.TryGetValue(word.Key, out var known))
            {
                return known.Id is null ? null : known;
            }
        }

        var found = new PersonPicture(null, null);
        try
        {
            if (_library.GetPerson(word.Text) is { } person)
            {
                found = new PersonPicture(person.Id, ImageTagOf(person));
            }
        }
#pragma warning disable CA1031 // A photo is decoration: no failure to find one may fail the cloud.
        catch (Exception)
#pragma warning restore CA1031
        {
            found = new PersonPicture(null, null);
        }

        lock (_peopleLock)
        {
            if (_peopleGeneration == generation)
            {
                _people[word.Key] = found;
            }
        }

        return found.Id is null ? null : found;
    }

    // The tag of the title's poster, as core's DtoService gives it in ImageTags.Primary, so the client's image URL is the one
    // the library's own cards use (and cached alike). Only for the page asked for, from titles the mask already let through;
    // a title gone from the library since the index was built, or one whose image can't be read, gets none (an icon card).
    private string? PrimaryImageTag(Guid id)
    {
        if (_images is null)
        {
            return null;
        }

        try
        {
            return ImageTagOf(_library.GetItemById(id));
        }
#pragma warning disable CA1031 // A poster is decoration: no failure to read one may fail the titles.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    // The primary image's cache tag, or null for none (or no image processor).
    private string? ImageTagOf(BaseItem? item)
    {
        var image = item?.GetImageInfo(ImageType.Primary, 0);
        return _images is null || item is null || image is null ? null : _images.GetImageCacheTag(item, image);
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

    private sealed record PersonPicture(Guid? Id, string? ImageTag);

    private sealed class CachedMask(bool[] titles, int count)
    {
        public bool[] Titles { get; } = titles;

        public int Count { get; } = count;

        public long LastUse { get; set; }
    }
}
