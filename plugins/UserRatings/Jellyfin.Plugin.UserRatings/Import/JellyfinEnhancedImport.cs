using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Interop;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.UserRatings.Configuration;
using Jellyfin.Plugin.UserRatings.Domain;
using Jellyfin.Plugin.UserRatings.Lifecycle;
using Jellyfin.Plugin.UserRatings.Services;
using Jellyfin.Plugin.UserRatings.Storage;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.UserRatings.Import;

/// <summary>
/// The Jellyfin Enhanced review importer: JE's <c>reviews.json</c>, read-only through the kit's
/// <see cref="JellyfinEnhancedReader"/>, into ratings of this plugin, as a data job with a dry run and a <c>PlanToken</c>.
/// </summary>
/// <remarks>
/// <para><b>Read and resolve</b> (outside the writer lock): each entry's key is split (<c>&lt;userIdN&gt;:&lt;movie|tv&gt;:&lt;tmdbKey&gt;</c>,
/// the TMDb key <c>&lt;tmdb&gt;</c>, <c>&lt;tmdb&gt;:s&lt;N&gt;</c> or <c>&lt;tmdb&gt;:s&lt;N&gt;:e&lt;M&gt;</c>, which JE 12.14 also writes whole into
/// the body's <c>TmdbId</c>: the series' id is the part before <c>:s</c>, the season and episode the key's); the body wins over the key
/// (<c>KeyMismatch</c>). The user is the server's, else the request's <c>UserMap</c>'s, else <c>UnknownUser</c>. The item is the
/// Movie or Series with the TMDb id (the season or episode under it by number), merged versions collapsed; several copies:
/// <c>Copies = First</c> picks one the reviewer can see, then the oldest, then the lowest path, <c>All</c> rates each. None:
/// stored detached (<c>Unmatched</c>) unless <c>StoreUnmatched</c> is off. Stars are <c>round(Rating × 2)</c> in 1..10
/// (<c>Adjusted</c>; a rating of 0 is none, as in JE), the text normalised as a member's; neither is <c>Empty</c>. Times are kept, but none later than the
/// import (<c>Clamped</c>).</para>
/// <para><b>Write</b> (one transaction under the writer lock, after the pre-operation <c>import</c> snapshot): the ledger
/// (<c>Source = JE</c>, the entry's key, JE's own <c>UpdatedAt</c>) decides. A ledger row whose rating has a tombstone is
/// <c>SkippedDeleted</c> (<c>PreviouslyPurged</c> after a Purge, made again only with <c>RecreatePurged</c>); an archived one
/// <c>AlreadyImportedArchived</c>; the same JE time <c>AlreadyImported</c>; a newer one updates a row unchanged since the import
/// (<c>Updated</c>), else <c>SkippedNewer</c>. Without a ledger: a tombstone of the reviewer on the item at or after JE's time is
/// <c>SkippedDeleted</c> too; the reviewer's own rating there is a <c>Conflict</c>, the newer winning, and only a write records
/// a ledger row; two keys on one reviewer and item, the newer wins (<c>DuplicateInSource</c>).
/// One <c>ImportJe</c> log row; nothing of JE's is written, and Jellyfin's own user data is never read or written (JE's
/// <c>MirrorReviewRatingsToJellyfin</c> values stay as JE left them, and are never a second rating of a review).</para>
/// </remarks>
internal sealed class JellyfinEnhancedImport
{
    /// <summary>The ledger's source for JE entries (key: the entry's key in <c>reviews.json</c>).</summary>
    public const string LedgerSource = "JE";

    /// <summary>The job's kind.</summary>
    public const string JobKind = "ImportJellyfinEnhanced";

    /// <summary>The 404's code when there is no <c>reviews.json</c>.</summary>
    public const string NotFoundCode = "JeDataNotFound";

    /// <summary>The 404's words.</summary>
    public const string NotFoundMessage = "Jellyfin Enhanced's reviews weren't found: its data folder (plugins/configurations/Jellyfin.Plugin.JellyfinEnhanced/) has no reviews.json.";

    /// <summary>The 400's code for a file of another shape.</summary>
    public const string FormatCode = "UnsupportedFormat";

    /// <summary>Where the report says the file is, under the plugin configurations folder.</summary>
    public const string SourceFile = JellyfinEnhancedFiles.ConfigurationName + "/" + JellyfinEnhancedFiles.ReviewsFile;

    /// <summary>The most samples, unknown users and left-out entries a report lists.</summary>
    public const int MaxListed = 100;

    /// <summary>The most samples a report lists.</summary>
    public const int MaxSamples = 20;

    /// <summary>The note next to <c>MirrorReviewRatingsToJellyfin</c> while it is on.</summary>
    public const string MirrorOnNote = "Jellyfin Enhanced's Mirror review ratings to Jellyfin is on: it has written each review's stars into Jellyfin's own user rating of the item. The import reads reviews.json only and never touches Jellyfin's user data, so those values stay as Jellyfin Enhanced left them, and no review is counted twice. Turn the mirror off after the switch (see the checklist).";

    /// <summary>The note next to <c>MirrorReviewRatingsToJellyfin</c> while it is off.</summary>
    public const string MirrorOffNote = "Jellyfin Enhanced's Mirror review ratings to Jellyfin is off. The import reads reviews.json only and never touches Jellyfin's user data.";

    /// <summary>The note on spoilers.</summary>
    public const string SpoilerNote = "Jellyfin Enhanced has no spoiler flag per review, so no imported review is marked as a spoiler; members can mark their own. Its Strip spoilers from reviews setting applies to its own section only.";

    // The JE review settings the report shows, in the checklist's order (JE 12.14).
    private static readonly string[] _reportedFlags =
    [
        "ShowUserReviews", "ShowReviews", "ShowAnilistReviews", "ReviewTranslationEnabled", "ReviewsExpandedByDefault",
        "MirrorReviewRatingsToJellyfin", "HideReviewsFromHiddenUsers", "HideReviewsFromDisabledUsers", "ActivityFeedShowReviewed",
        "SpoilerStripReviews", "ShowUserRatingOnPosters"
    ];

    private readonly IRatingsStore _store;
    private readonly ILibraryManager _library;
    private readonly IUserManager _users;
    private readonly Func<JellyfinEnhancedReader> _reader;
    private readonly Func<DetachedRecords> _detached;
    private readonly Func<RatingsLibraryConsumer> _owner;
    private readonly ArchivedLinePurges _archivedLines;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyfinEnhancedImport"/> class.
    /// </summary>
    /// <param name="store">The store.</param>
    /// <param name="library">The library.</param>
    /// <param name="users">The users.</param>
    /// <param name="reader">The kit's JE reader.</param>
    /// <param name="detached">The kit's detached records (the archive).</param>
    /// <param name="owner">The ratings' record owner.</param>
    /// <param name="archivedLines">The archive's lines by rating id.</param>
    /// <param name="clock">The clock.</param>
    /// <param name="logger">The logger.</param>
    public JellyfinEnhancedImport(
        IRatingsStore store,
        ILibraryManager library,
        IUserManager users,
        Func<JellyfinEnhancedReader> reader,
        Func<DetachedRecords> detached,
        Func<RatingsLibraryConsumer> owner,
        ArchivedLinePurges archivedLines,
        TimeProvider clock,
        ILogger logger)
    {
        _store = store;
        _library = library;
        _users = users;
        _reader = reader;
        _detached = detached;
        _owner = owner;
        _archivedLines = archivedLines;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Reads JE's file and settings, before the job starts (so a missing or foreign file is the route's 404 or 400).
    /// </summary>
    /// <returns>The file and the settings.</returns>
    /// <exception cref="PluginException">404 no <c>reviews.json</c>; 400 a file of another shape or that can't be read.</exception>
    public (JeReviewsFile File, JeSettings Settings) Read()
    {
        var reader = _reader();
        var file = reader.ReadReviews();
        return file.Format switch
        {
            JeDataFormat.Missing => throw new PluginException(PluginErrorKind.NotFound, NotFoundMessage) { Code = NotFoundCode },
            JeDataFormat.Reviews1 => (file, reader.ReadSettings()),
            _ => throw new PluginException(PluginErrorKind.Invalid, "Jellyfin Enhanced's reviews.json can't be imported: " + (file.Problem ?? "unsupported Jellyfin Enhanced format.")) { Code = FormatCode }
        };
    }

    /// <summary>
    /// Runs the import of a file read by <see cref="Read"/>, as a job.
    /// </summary>
    /// <param name="file">JE's reviews.</param>
    /// <param name="settings">JE's settings, for the report.</param>
    /// <param name="request">The request.</param>
    /// <param name="ifPlan">The dry run's <c>PlanToken</c>; required for a real run.</param>
    /// <param name="adminId">The admin, for the moderation log; null for an API key.</param>
    /// <param name="job">The job.</param>
    /// <param name="cancellationToken">Cancels it before the write phase.</param>
    /// <returns>The report.</returns>
    /// <exception cref="PluginException">409 read-only data or <see cref="RatingsEnvelope.ChangedCode"/>; 503.</exception>
    public async Task<IDictionary<string, object?>> ImportAsync(JeReviewsFile file, JeSettings settings, JeImportRequest request, string? ifPlan, Guid? adminId, AdminJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(job);
        _store.ThrowIfReadOnly();
        var now = _clock.GetUtcNow().UtcDateTime;
        var ceiling = ImportTimes.Ceiling(now);
        var candidates = new List<Candidate>(file.Entries.Count);
        for (var i = 0; i < file.Entries.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            job.Progress("Resolve", i, file.Entries.Count);
            candidates.Add(Resolve(file.Entries[i], request, ceiling));
        }

        MarkDuplicates(candidates);
        var view = _archivedLines.Look();
        job.EnterWritePhase(candidates.Count);
        var origin = RatingValues.DeletionOrigin.Import(job.Id);
        void Compare(Applied result)
        {
            if (!request.DryRun && !string.Equals(ifPlan?.Trim(), result.PlanToken, StringComparison.OrdinalIgnoreCase))
            {
                throw new PluginException(PluginErrorKind.Conflict, RatingsEnvelope.ChangedMessage) { Code = RatingsEnvelope.ChangedCode };
            }
        }

        // A real run compares its plan first, rolled back, before the import snapshot (a review of 0.3.21 found that a
        // refused run took one); the write compares again, in the same hold of the writer lock, so it always agrees.
        var (applied, safety) = await _store.RunWriteAsync(
            w =>
            {
                var result = Apply(w, candidates, request, adminId, view, now, origin);
                Compare(result);
                if (!request.DryRun)
                {
                    // The archive as the import found it, beside the database's snapshot (as the envelope import does): a
                    // restore of that snapshot puts back the lines of the reviews the import replaced too.
                    _detached().ArchiveOf(_owner()).CopyToSnapshots(_detached().SnapshotDirectory, SnapshotOperations.Import, RatingsUserDataOwner.PreOpKeepWithin);
                }

                return result;
            },
            commit: !request.DryRun,
            preOpOperation: request.DryRun ? null : SnapshotOperations.Import,
            check: request.DryRun ? null : w => Compare(Apply(w, candidates, request, adminId, view, now, origin)),
            CancellationToken.None).ConfigureAwait(false);
        job.LeaveWritePhase();
        var safetyName = safety is null ? null : Path.GetFileName(safety);
        if (!request.DryRun)
        {
            job.MarkCommitted(safetyName);
            if (applied.ArchivePurges > 0)
            {
                try
                {
                    await _archivedLines.RunAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Jellyfin Enhanced import: the archive lines of the reviews it replaced stay pending; the daily maintenance takes them out");
                }
            }

            RatingsCheckpoint.RequestAfter(_store, _logger, "the Jellyfin Enhanced import");
            _logger.LogInformation(
                "Imported Jellyfin Enhanced's reviews (Copies {Copies}): {Outcomes}; snapshot before: {Snapshot}",
                request.Copies,
                string.Join(", ", applied.Outcomes.OrderBy(o => o.Key, StringComparer.Ordinal).Select(o => o.Key + " " + o.Value.ToString(CultureInfo.InvariantCulture))),
                safetyName ?? "none");
        }

        return Report(file, settings, request, candidates, applied, safetyName);
    }

    /// <summary>
    /// Splits a JE key: <c>&lt;userIdN&gt;:&lt;movie|tv&gt;:&lt;tmdbKey&gt;</c> (JE's <c>reviews.js</c> key builder), the
    /// TMDb key as <see cref="ParseTmdbKey"/> reads it.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <returns>Its parts; null for a key of another shape.</returns>
    internal static (string User, string MediaType, string Tmdb, int? Season, int? Episode)? ParseKey(string key)
    {
        var parts = (key ?? string.Empty).Split(':', 3);
        if (parts.Length < 3 || parts.Take(2).Any(string.IsNullOrWhiteSpace) || ParseTmdbKey(parts[2]) is not { } tmdbKey)
        {
            return null;
        }

        return (parts[0].Trim(), parts[1].Trim(), tmdbKey.Tmdb, tmdbKey.Season, tmdbKey.Episode);
    }

    /// <summary>
    /// Splits a JE TMDb key: <c>&lt;tmdb&gt;</c>, <c>&lt;tmdb&gt;:s&lt;N&gt;</c> or <c>&lt;tmdb&gt;:s&lt;N&gt;:e&lt;M&gt;</c>, with the
    /// series' TMDb id for a season or an episode. JE 12.14 writes it whole into an entry's <c>TmdbId</c> as well as into
    /// its key (a review posted to <c>reviews/tv/777001:s1:e2</c> has <c>"TmdbId": "777001:s1:e2"</c>);
    /// a file of another tool may have the series' id alone there.
    /// </summary>
    /// <param name="text">The TMDb key.</param>
    /// <returns>The series' (or movie's) TMDb id, the season and the episode; null for text of another shape.</returns>
    internal static (string Tmdb, int? Season, int? Episode)? ParseTmdbKey(string? text)
    {
        var parts = (text ?? string.Empty).Split(':');
        if (parts.Length > 3 || string.IsNullOrWhiteSpace(parts[0]))
        {
            return null;
        }

        int? season = null, episode = null;
        if (parts.Length >= 2)
        {
            if (!TryNumber(parts[1].Trim(), 's', out var s))
            {
                return null;
            }

            season = s;
        }

        if (parts.Length == 3)
        {
            if (!TryNumber(parts[2].Trim(), 'e', out var e))
            {
                return null;
            }

            episode = e;
        }

        return (parts[0].Trim(), season, episode);
    }

    /// <summary>
    /// JE's stars as this plugin's half stars: <c>round(Rating × 2)</c> in 1..10. A rating of exactly 0 is no rating, as JE
    /// itself takes it (JE 12.14 saves <c>rating || null</c>, shows no stars for 0 and leaves
    /// it out of its average), never half a star; one above 0 that rounds to nothing is half a star (<c>Adjusted</c>).
    /// </summary>
    /// <param name="rating">JE's rating.</param>
    /// <param name="adjusted">Whether it needed rounding or clamping.</param>
    /// <returns>The half stars; null for no rating.</returns>
    internal static int? Stars2(double? rating, out bool adjusted)
    {
        adjusted = false;
        if (rating is not { } r || double.IsNaN(r) || double.IsInfinity(r))
        {
            adjusted = rating is not null;
            return null;
        }

        if (r == 0)
        {
            return null;
        }

        var doubled = r * 2;
        var rounded = (int)Math.Clamp(Math.Round(doubled, MidpointRounding.AwayFromZero), 1, 10);
        adjusted = Math.Abs(rounded - doubled) > 1e-9;
        return rounded;
    }

    /// <summary>
    /// Reads a JE time: with an offset or <c>Z</c>, converted to UTC; without, taken as UTC; to the millisecond.
    /// </summary>
    /// <param name="text">The time as JE wrote it.</param>
    /// <returns>The time; null for none or one that can't be read.</returns>
    internal static DateTime? ParseTime(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)
            || !DateTimeOffset.TryParse(text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out var parsed))
        {
            return null;
        }

        return RatingsService.ToMilliseconds(parsed.UtcDateTime);
    }

    // The item id of a detached row from JE whose item isn't in the library: a hash of its kind and TMDb key.
    private static Guid PlaceholderItemId(string kind, string tmdb, int? season, int? episode)
    {
        var text = string.Create(CultureInfo.InvariantCulture, $"jellyfin-enhanced|{kind}|tmdb|{tmdb}|{season}|{episode}");
        return new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(text)).AsSpan(0, 16));
    }

    private static bool TryNumber(string part, char prefix, out int value)
    {
        value = 0;
        return part.Length > 1 && part[0] == prefix && int.TryParse(part.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= 0;
    }

    private static string Describe(BaseItem item)
        => item.ProductionYear is { } year ? string.Create(CultureInfo.InvariantCulture, $"{item.Name} ({year})") : item.Name ?? string.Empty;

    private static string Hash(string? text)
        => text is null ? string.Empty : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)).AsSpan(0, 8));

    // Two keys on one reviewer and the same item(s): the newer JE time wins (then the later key), the others are left out.
    private static void MarkDuplicates(List<Candidate> candidates)
    {
        foreach (var group in candidates.Where(c => c.Outcome is null && c.Targets.Count > 0).GroupBy(c => c.UserId.ToString("N", CultureInfo.InvariantCulture) + "|" + c.TargetKey, StringComparer.Ordinal))
        {
            var winner = group.OrderByDescending(c => c.Raw).ThenByDescending(c => c.Key, StringComparer.Ordinal).First();
            foreach (var loser in group.Where(c => !ReferenceEquals(c, winner)))
            {
                loser.Outcome = "DuplicateInSource";
            }
        }
    }

    private static void Count(Dictionary<string, int> outcomes, string outcome) => outcomes[outcome] = outcomes.GetValueOrDefault(outcome) + 1;

    private Candidate Resolve(JeReview entry, JeImportRequest request, DateTime ceiling)
    {
        var c = new Candidate { Key = entry.Key };
        var key = ParseKey(entry.Key);
        var userText = entry.UserId ?? key?.User;
        var mediaType = (entry.MediaType ?? key?.MediaType)?.Trim().ToLowerInvariant();

        // The body's TmdbId: the key's whole TMDb key, as JE 12.14 writes it (the series' id, then :sN and :eM), or the
        // series' id alone. The series' (or movie's) id is the part before ":s", the body's when it has one (the body wins
        // over the key); the season and episode are the key's, else the body's. A body that can't be split is an id as it is.
        var bodyTmdb = entry.TmdbId?.Trim();
        (string Tmdb, int? Season, int? Episode)? body = bodyTmdb is null ? null : ParseTmdbKey(bodyTmdb) ?? (bodyTmdb, null, null);
        var tmdb = body?.Tmdb ?? key?.Tmdb;
        var season = key is not null ? key.Value.Season : body?.Season;
        var episode = key is not null ? key.Value.Episode : body?.Episode;
        if (key is { } k)
        {
            // The body equal to the key's whole TMDb key, or to its series' id alone, agrees with it.
            c.KeyMismatch = (entry.UserId is { } u && !(Guid.TryParse(u, out var a) && Guid.TryParse(k.User, out var b) && a == b))
                || (entry.MediaType is { } m && !string.Equals(m.Trim(), k.MediaType, StringComparison.OrdinalIgnoreCase))
                || (body is { } t && (!string.Equals(t.Tmdb, k.Tmdb, StringComparison.Ordinal) || (t.Season is not null && (t.Season != k.Season || t.Episode != k.Episode))));
        }

        if (!Guid.TryParse(userText, out var jeUser) || jeUser == Guid.Empty)
        {
            return c.LeftOut("Invalid", "No user id.");
        }

        c.JeUserId = jeUser.ToString("N", CultureInfo.InvariantCulture);
        if (mediaType is not ("movie" or "tv"))
        {
            return c.LeftOut("Invalid", "The media type isn't movie or tv.");
        }

        if (string.IsNullOrEmpty(tmdb))
        {
            return c.LeftOut("Invalid", "No TMDb id.");
        }

        if (mediaType == "movie" && (season is not null || body?.Season is not null))
        {
            return c.LeftOut("Invalid", "A movie's key has a season.");
        }

        c.Stars2 = Stars2(entry.Rating, out var adjusted);
        c.Adjusted = adjusted && c.Stars2 is not null;
        c.Text = ReviewText.Normalize(entry.Content);
        if (c.Stars2 is null && c.Text is null)
        {
            return c.LeftOut("Empty", null);
        }

        if (c.Text is { Length: > RatingsSettings.MaxReviewLimit })
        {
            return c.LeftOut("Invalid", string.Create(CultureInfo.InvariantCulture, $"The review is longer than {RatingsSettings.MaxReviewLimit} characters."));
        }

        var updated = ParseTime(entry.UpdatedAt) ?? ParseTime(entry.CreatedAt);
        if (updated is not { } raw)
        {
            return c.LeftOut("Invalid", "No time it was written that can be read.");
        }

        var created = ParseTime(entry.CreatedAt) is { } made && made < raw ? made : raw;
        c.Raw = raw;
        c.Updated = ImportTimes.Clamp(raw, ceiling);
        c.Created = ImportTimes.Clamp(created, ceiling);
        c.Clamped = ImportTimes.IsAhead(raw, ceiling) || ImportTimes.IsAhead(created, ceiling);

        var user = _users.GetUserById(jeUser);
        if (user is null && request.UserMap.TryGetValue(c.JeUserId, out var mapped))
        {
            user = _users.GetUserById(mapped);
        }

        if (user is null)
        {
            return c.LeftOut("UnknownUser", null);
        }

        c.UserId = user.Id;
        c.UserName = user.Username;
        c.Hidden = user.HasPermission(PermissionKind.IsHidden);
        c.Disabled = user.HasPermission(PermissionKind.IsDisabled);

        var matches = Find(mediaType, tmdb, season, episode);
        c.Matches = matches.Count;
        if (matches.Count == 0)
        {
            if (!request.StoreUnmatched)
            {
                return c.LeftOut("Unmatched", null);
            }

            var kind = mediaType == "movie" ? "Movie" : episode is not null ? "Episode" : season is not null ? "Season" : "Series";
            var ids = new Dictionary<string, string>(StringComparer.Ordinal) { ["Tmdb"] = tmdb };
            // A detached row keeps an item id: one made from the entry's TMDb key, the same at every run (never an item's).
            var placeholder = PlaceholderItemId(kind, tmdb, season, episode);
            var identity = kind is "Movie" or "Series"
                ? new IdentitySnapshot { ItemId = placeholder, Kind = kind, Name = "TMDb " + tmdb, ProviderIds = ids }
                : new IdentitySnapshot { ItemId = placeholder, Kind = kind, Name = "TMDb " + tmdb, SeriesProviderIds = ids, Season = season, Episode = episode };
            c.Targets.Add(new Target(null, identity));
            c.TargetKey = string.Join(",", ItemIdentity.StrongKeys(identity));
            c.ItemName = identity.Name + (season is null ? string.Empty : string.Create(CultureInfo.InvariantCulture, $" S{season:D2}")) + (episode is null ? string.Empty : string.Create(CultureInfo.InvariantCulture, $"E{episode:D2}"));
            return c;
        }

        var visible = IdentityResolver.VisibleTo(_library, user);
        var ordered = matches
            .OrderBy(i => visible(i) ? 0 : 1)
            .ThenBy(i => i.DateCreated)
            .ThenBy(i => i.Path ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(i => i.Id)
            .ToList();
        var chosen = request.Copies == JeImportRequest.All ? ordered : ordered.Take(1).ToList();
        foreach (var item in chosen)
        {
            c.Targets.Add(new Target(item.Id, ItemIdentity.Capture(item, _library)));
        }

        c.AlsoMatched = ordered.Skip(chosen.Count).Select(i => i.Id).ToList();
        c.TargetKey = string.Join(",", c.Targets.Select(t => t.ItemId!.Value.ToString("N", CultureInfo.InvariantCulture)).Order(StringComparer.Ordinal));
        c.ItemName = Describe(ordered[0]);
        return c;
    }

    // The items with the TMDb id: the Movie, or the Series and the Season or Episode under it by number; not virtual (a
    // missing episode), in a library, merged versions collapsed to the one that stands for them.
    private List<BaseItem> Find(string mediaType, string tmdb, int? season, int? episode)
    {
        var tops = _library.GetItemList(new InternalItemsQuery
        {
            HasAnyProviderId = new Dictionary<string, string> { ["Tmdb"] = tmdb },
            IncludeItemTypes = [mediaType == "movie" ? BaseItemKind.Movie : BaseItemKind.Series],
            Recursive = true
        });
        var found = new List<BaseItem>();
        if (mediaType == "movie" || season is null)
        {
            found.AddRange(tops);
        }
        else
        {
            foreach (var series in tops)
            {
                var query = new InternalItemsQuery
                {
                    AncestorIds = [series.Id],
                    IncludeItemTypes = [episode is null ? BaseItemKind.Season : BaseItemKind.Episode],
                    Recursive = true
                };
                if (episode is null)
                {
                    query.IndexNumber = season;
                }
                else
                {
                    query.ParentIndexNumber = season;
                    query.IndexNumber = episode;
                }

                found.AddRange(_library.GetItemList(query));
            }
        }

        var inLibrary = found.Where(i => !i.IsVirtualItem && LibraryPresence.IsInLibrary(_library, i)).GroupBy(i => i.Id).Select(g => g.First()).ToList();
        if (inLibrary.Count < 2)
        {
            return inLibrary;
        }

        var lookup = VersionSets.LibraryLookup(_library);
        var representatives = VersionSets.Collapse(
            inLibrary.Select(VersionMember.From).ToList(),
            ids => ids.Where(id => LibraryPresence.IsInLibrary(_library, id)).ToHashSet(),
            lookup,
            VersionSets.LibraryPaths(_library));
        var byId = inLibrary.ToDictionary(i => i.Id);
        return representatives
            .Select(r => byId.TryGetValue(r.Id, out var item) ? item : _library.GetItemById(r.Id))
            .Where(i => i is not null)
            .Select(i => i!)
            .ToList();
    }

    private Applied Apply(IRatingsWriter w, List<Candidate> candidates, JeImportRequest request, Guid? adminId, ArchivedLinePurges.ArchiveView view, DateTime now, string origin)
    {
        var outcomes = new Dictionary<string, int>(StringComparer.Ordinal);
        var plan = new StringBuilder("ur-je-import/1|").Append(request.Copies).Append('|').Append(request.StoreUnmatched).Append('|').Append(request.RecreatePurged).Append('\n');
        var textReplaced = new List<Guid>();
        int created = 0, updated = 0;
        foreach (var c in candidates)
        {
            c.RowOutcomes.Clear();
            if (c.Outcome is not null)
            {
                Count(outcomes, c.Outcome);
                c.RowOutcomes.Add(c.Outcome);
            }
            else
            {
                var (rowsCreated, rowsUpdated) = ApplyEntry(w, c, request, view, now, textReplaced);
                created += rowsCreated;
                updated += rowsUpdated;
                foreach (var outcome in c.RowOutcomes)
                {
                    Count(outcomes, outcome);
                }
            }

            plan.Append(c.Key).Append('|').Append(string.Join(",", c.RowOutcomes)).Append('|').Append(c.UserId.ToString("N", CultureInfo.InvariantCulture)).Append('|')
                .Append(c.TargetKey).Append('|').Append(c.Stars2?.ToString(CultureInfo.InvariantCulture)).Append('|').Append(Hash(c.Text)).Append('|')
                .Append(c.Raw.Ticks.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        foreach (var (from, to) in request.UserMap.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            plan.Append("U:").Append(from).Append(':').Append(to.ToString("N", CultureInfo.InvariantCulture)).Append('\n');
        }

        var skipped = outcomes.Where(o => o.Key is not ("Created" or "StoredDetached" or "Updated" or "ConflictTookJe")).Sum(o => o.Value);
        w.AppendModerationLog(new ModerationLogRecord
        {
            AtUtc = now,
            ActorUserId = adminId,
            Action = RatingValues.ModerationAction.ImportJe,
            Details = string.Create(
                CultureInfo.InvariantCulture,
                $"{{\"Source\":\"{LedgerSource}\",\"Copies\":\"{request.Copies}\",\"Entries\":{candidates.Count},\"Created\":{created},\"Updated\":{updated},\"Skipped\":{skipped},\"Origin\":\"{origin}\"}}")
        });
        var purges = textReplaced.Count == 0 ? 0 : ArchivedLinePurges.Record(w, view, textReplaced);
        var token = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plan.ToString())).AsSpan(0, 16)).ToLowerInvariant();
        return new Applied(outcomes, token, purges, created, updated);
    }

    // One entry's rows: the ledger's first, then the copies it hasn't (Copies = All), or the one copy without a ledger. A
    // detached row in the ledger covers every copy: it is the entry's rating, waiting on the
    // Detached list for the admin to say which copy it belongs to (it stays detached while two copies match), so Copies =
    // All adds none beside it. Before, it added one on every copy, three ratings for one JE review; once the admin attaches
    // it, the next Copies = All run adds the other copies.
    private (int Created, int Updated) ApplyEntry(IRatingsWriter w, Candidate c, JeImportRequest request, ArchivedLinePurges.ArchiveView view, DateTime now, List<Guid> textReplaced)
    {
        int created = 0, updated = 0;
        var link = w.GetImport(LedgerSource, c.Key);
        var rows = new List<Guid>();
        var covered = new HashSet<Guid>();
        var hasLedger = false;
        var detached = false;
        var wrote = false;
        foreach (var rid in link?.RatingIds ?? [])
        {
            if (w.GetTombstone(rid) is { } tombstone)
            {
                if (tombstone.DeletedBy == RatingValues.DeletedBy.Purge && request.RecreatePurged)
                {
                    // Made again below on the copy the entry resolves to; the purged rating's own link goes with it.
                    continue;
                }

                hasLedger = true;
                covered.Add(tombstone.ItemId);
                rows.Add(rid);
                c.RowOutcomes.Add(tombstone.DeletedBy == RatingValues.DeletedBy.Purge ? "PreviouslyPurged" : "SkippedDeleted");
                continue;
            }

            if (w.Get(rid) is not { } row)
            {
                if (view.Mentions(rid))
                {
                    hasLedger = true;
                    rows.Add(rid);
                    c.RowOutcomes.Add("AlreadyImportedArchived");
                }

                // Otherwise gone without a tombstone (a Replace's wipe): no longer the entry's row.
                continue;
            }

            hasLedger = true;
            rows.Add(rid);
            if (row.DetachedUtc is null)
            {
                covered.Add(row.ItemId);
            }
            else
            {
                detached = true;
            }

            if (c.Raw <= link!.SourceUpdatedUtc)
            {
                c.RowOutcomes.Add("AlreadyImported");
            }
            else if (row.UpdatedUtc == ImportTimes.Written(link))
            {
                Write(w, row, c, textReplaced);
                c.RowOutcomes.Add("Updated");
                updated++;
                wrote = true;
            }
            else
            {
                c.RowOutcomes.Add("SkippedNewer");
            }
        }

        var targets = !hasLedger ? c.Targets : request.Copies == JeImportRequest.All && !detached ? c.Targets.Where(t => t.ItemId is { } id && !covered.Contains(id)).ToList() : [];
        foreach (var target in targets)
        {
            if (target.ItemId is { } itemId)
            {
                // Deleted here since JE's copy was written, without a ledger row to say so (a Replace's file without the JE
                // ledger, say): never brought back.
                if (w.GetTombstonesOf(c.UserId, itemId).Any(t => t.DeletedUtc >= c.Raw && !(request.RecreatePurged && t.DeletedBy == RatingValues.DeletedBy.Purge)))
                {
                    c.RowOutcomes.Add("SkippedDeleted");
                    continue;
                }

                if (w.GetAttached(c.UserId, itemId) is { } own)
                {
                    if (own.UpdatedUtc >= c.Updated)
                    {
                        // The member's own rating is newer: kept, and no ledger row (a match this import didn't write).
                        c.RowOutcomes.Add("ConflictKeptLocal");
                    }
                    else
                    {
                        Write(w, own, c, textReplaced);
                        rows.Add(own.Id);
                        c.RowOutcomes.Add("ConflictTookJe");
                        updated++;
                        wrote = true;
                    }

                    continue;
                }
            }

            var id = Guid.NewGuid();
            var attached = target.ItemId is not null;
            w.Upsert(new RatingRecord
            {
                Id = id,
                UserId = c.UserId,
                Identity = target.Identity,
                Stars2 = c.Stars2,
                ReviewText = c.Text,
                IsSpoiler = false,
                CreatedUtc = c.Created,
                UpdatedUtc = c.Updated,
                ReviewUpdatedUtc = c.Text is null ? null : c.Updated,
                Origin = RatingValues.Origin.JellyfinEnhanced,
                DetachedUtc = attached ? null : now,
                DetachReason = attached ? null : RatingValues.DetachReason.Unmatched,
                AttachedUtc = attached ? now : null
            });
            rows.Add(id);
            c.RowOutcomes.Add(attached ? "Created" : "StoredDetached");
            created++;
            wrote = true;
        }

        if (c.RowOutcomes.Count == 0)
        {
            c.RowOutcomes.Add("AlreadyImported");
        }

        if (wrote)
        {
            // JE's own time (the row's is no later than the import's; ImportTimes.Written gives it back).
            w.RecordImport(new ImportLedgerRecord(LedgerSource, c.Key, c.Raw, now, rows.Distinct().ToList()));
        }
        else if (link is not null && !rows.Order().SequenceEqual(link.RatingIds.Order()))
        {
            w.RecordImport(link with { RatingIds = rows.Distinct().ToList() });
        }

        return (created, updated);
    }

    // JE's values over a row (unchanged since an earlier import, or the member's own older one): the review it replaces
    // leaves the archive's lines too, as a member's own edit does.
    private static void Write(IRatingsWriter w, RatingRecord row, Candidate c, List<Guid> textReplaced)
    {
        var updated = c.Updated > row.UpdatedUtc ? c.Updated : row.UpdatedUtc.AddMilliseconds(1);
        var sameText = string.Equals(row.ReviewText, c.Text, StringComparison.Ordinal);
        w.Upsert(row with
        {
            Stars2 = c.Stars2,
            ReviewText = c.Text,
            IsSpoiler = sameText && row.IsSpoiler,
            UpdatedUtc = updated,
            ReviewUpdatedUtc = c.Text is null ? null : sameText ? row.ReviewUpdatedUtc : updated,
            CreatedUtc = c.Created < row.CreatedUtc ? c.Created : row.CreatedUtc
        });
        if (row.ReviewText is not null && !sameText)
        {
            textReplaced.Add(row.Id);
        }
    }

    private Dictionary<string, object?> Report(JeReviewsFile file, JeSettings settings, JeImportRequest request, List<Candidate> candidates, Applied applied, string? safetyName)
    {
        int Outcome(string name) => applied.Outcomes.GetValueOrDefault(name);
        var resolved = candidates.Where(c => c.Outcome is null or "DuplicateInSource").ToList();
        var multiple = candidates.Count(c => c.Matches > 1);
        var copiesNote = multiple == 0
            ? null
            : string.Create(CultureInfo.InvariantCulture, $"{multiple} {(multiple == 1 ? "review matches" : "reviews match")} more than one copy. In Jellyfin Enhanced a review showed on every copy with its TMDb id; ")
                + (request.Copies == JeImportRequest.All
                    ? "with Copies = All each copy gets its own rating, counted on each copy."
                    : "here each attaches to one copy (the one its member can see, then the oldest), and the other copies show the \"Also rated on another copy\" hint.");
        var mirror = settings.Flag("MirrorReviewRatingsToJellyfin");
        var notes = new List<string>();
        var clamped = candidates.Count(c => c.Clamped && c.Outcome is null);
        if (clamped > 0)
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture, $"{clamped} {(clamped == 1 ? "review was" : "reviews were")} dated ahead of this server's clock (Jellyfin Enhanced's times local, or its clock ahead), so the import dates them no later than the import."));
        }

        var jeSettings = new Dictionary<string, object?>(StringComparer.Ordinal) { ["SettingsFile"] = settings.Exists };
        foreach (var name in _reportedFlags)
        {
            jeSettings[name] = settings.Flag(name);
        }

        // Whether the translation service is set, never what it is.
        jeSettings["ReviewTranslationUrlSet"] = settings.IsSet("ReviewTranslationUrl");
        jeSettings["ReviewTranslationApiKeySet"] = settings.IsSet("ReviewTranslationApiKey");
        var report = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["DryRun"] = request.DryRun,
            ["SourceFile"] = SourceFile,
            ["Format"] = JeReviewsFile.FormatName,
            ["Copies"] = request.Copies,
            ["StoreUnmatched"] = request.StoreUnmatched,
            ["RecreatePurged"] = request.RecreatePurged,
            ["Total"] = file.Entries.Count + file.Invalid.Count,
            ["Created"] = request.DryRun ? 0 : Outcome("Created"),
            ["Updated"] = request.DryRun ? 0 : Outcome("Updated") + Outcome("ConflictTookJe"),
            ["WouldCreate"] = request.DryRun ? Outcome("Created") : 0,
            ["WouldUpdate"] = request.DryRun ? Outcome("Updated") + Outcome("ConflictTookJe") : 0,
            ["Matched"] = resolved.Count(c => c.Matches > 0),
            ["MatchedMultiple"] = multiple,
            ["Unmatched"] = candidates.Count(c => c.Outcome is null or "DuplicateInSource" or "Unmatched" && c.Matches == 0 && c.UserId != Guid.Empty),
            ["StoredDetached"] = Outcome("StoredDetached"),
            ["UnknownUser"] = Outcome("UnknownUser"),
            ["Empty"] = Outcome("Empty"),
            ["Invalid"] = Outcome("Invalid") + file.Invalid.Count,
            ["Adjusted"] = candidates.Count(c => c.Adjusted && c.Outcome is null),
            ["AlreadyImported"] = Outcome("AlreadyImported"),
            ["AlreadyImportedArchived"] = Outcome("AlreadyImportedArchived"),
            ["SkippedNewer"] = Outcome("SkippedNewer"),
            ["SkippedDeleted"] = Outcome("SkippedDeleted"),
            ["PreviouslyPurged"] = Outcome("PreviouslyPurged"),
            ["Conflict"] = Outcome("ConflictKeptLocal") + Outcome("ConflictTookJe"),
            ["ConflictKeptLocal"] = Outcome("ConflictKeptLocal"),
            ["ConflictTookJe"] = Outcome("ConflictTookJe"),
            ["DuplicateInSource"] = Outcome("DuplicateInSource"),
            ["KeyMismatch"] = candidates.Count(c => c.KeyMismatch),
            ["Clamped"] = clamped,
            ["HiddenUserReviews"] = candidates.Count(c => c.Hidden && c.Outcome is null),
            ["DisabledUserReviews"] = candidates.Count(c => c.Disabled && c.Outcome is null),
            ["CopiesNote"] = copiesNote,
            ["MirrorNote"] = mirror ? MirrorOnNote : MirrorOffNote,
            ["SpoilerNote"] = SpoilerNote,
            ["JeSettings"] = jeSettings,
            ["SourceFields"] = file.Fields,
            ["UnknownUserIds"] = candidates.Where(c => c.Outcome == "UnknownUser").GroupBy(c => c.JeUserId!, StringComparer.Ordinal)
                .Select(g => new Dictionary<string, object?>(StringComparer.Ordinal) { ["JeUserId"] = g.Key, ["Reviews"] = g.Count() })
                .Take(MaxListed).ToList(),
            ["LeftOut"] = file.Invalid.Select(p => new Dictionary<string, object?>(StringComparer.Ordinal) { ["Key"] = p.Key, ["Reason"] = p.Reason })
                .Concat(candidates.Where(c => c.Outcome == "Invalid").Select(c => new Dictionary<string, object?>(StringComparer.Ordinal) { ["Key"] = c.Key, ["Reason"] = c.Reason }))
                .Take(MaxListed).ToList(),
            ["Samples"] = candidates.Take(MaxSamples).Select(c => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["Key"] = c.Key,
                ["User"] = c.UserName,
                ["Item"] = c.ItemName,
                ["Stars"] = c.Stars2 is { } s ? s / 2.0 : null,
                ["HasReview"] = c.Text is not null,
                ["Outcome"] = string.Join(", ", c.RowOutcomes.Distinct()),
                ["AlsoMatched"] = c.AlsoMatched.Count
            }).ToList(),
            ["Outcomes"] = applied.Outcomes.OrderBy(o => o.Key, StringComparer.Ordinal).ToDictionary(o => o.Key, o => (object?)o.Value, StringComparer.Ordinal),
            ["Notes"] = notes,
            ["PlanToken"] = applied.PlanToken,
            ["SafetySnapshot"] = safetyName
        };
        return report;
    }

    private sealed record Target(Guid? ItemId, IdentitySnapshot Identity);

    private sealed record Applied(Dictionary<string, int> Outcomes, string PlanToken, int ArchivePurges, int Created, int Updated);

    private sealed class Candidate
    {
        public required string Key { get; init; }

        public string? Outcome { get; set; }

        public string? Reason { get; set; }

        public string? JeUserId { get; set; }

        public Guid UserId { get; set; }

        public string? UserName { get; set; }

        public bool Hidden { get; set; }

        public bool Disabled { get; set; }

        public bool KeyMismatch { get; set; }

        public int? Stars2 { get; set; }

        public bool Adjusted { get; set; }

        public string? Text { get; set; }

        public DateTime Raw { get; set; }

        public DateTime Created { get; set; }

        public DateTime Updated { get; set; }

        public bool Clamped { get; set; }

        public int Matches { get; set; }

        public List<Target> Targets { get; } = [];

        public string TargetKey { get; set; } = string.Empty;

        public List<Guid> AlsoMatched { get; set; } = [];

        public string? ItemName { get; set; }

        public List<string> RowOutcomes { get; } = [];

        public Candidate LeftOut(string outcome, string? reason)
        {
            Outcome = outcome;
            Reason = reason;
            return this;
        }
    }
}
