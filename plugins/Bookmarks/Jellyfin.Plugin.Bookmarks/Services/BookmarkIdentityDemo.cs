using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Bookmarks.Configuration;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.Storage;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.Bookmarks.Services;

/// <summary>
/// The developer demo of the kit's item identity: the kit's <see cref="ItemIdentity"/>,
/// <see cref="IdentityResolver"/> and <see cref="MoveJournalReader"/> behind admin endpoints, so a sandbox can check them
/// against a real library (<c>scripts/identity-probe.mjs</c>). It keeps no state and writes nothing. Every call answers
/// 403 "turned off" unless the developer setting <c>EnableIdentityDemo</c> is on. Bookmarks' own identity service
/// uses the same kit services.
/// </summary>
/// <remarks>
/// Admin endpoints are not exempt from the kit's privacy rules: ids resolve as the calling admin (<c>GetItemById&lt;BaseItem&gt;(id,
/// admin)</c>), the resolver sees only the items that admin can (its <c>isVisible</c> check), and journal lines that name
/// an item the admin can't see are left out; a line's <c>NewPath</c> is shown only with an item the caller can see. An API
/// key (no caller) works over the whole server. The journal's counts are of the whole file.
/// </remarks>
internal sealed class BookmarkIdentityDemo : IBookmarkIdentityDemo
{
    private readonly IPluginConfigurationProvider _configuration;
    private readonly ILibraryManager _library;
    private readonly IdentityResolver _resolver;
    private readonly MoveJournalReader _moves;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="BookmarkIdentityDemo"/> class.
    /// </summary>
    /// <param name="configuration">The plugin settings.</param>
    /// <param name="library">The library manager.</param>
    /// <param name="resolver">The kit's identity resolver.</param>
    /// <param name="moves">The kit's move-journal reader.</param>
    /// <param name="timeProvider">The clock for pending relinks; the system clock by default.</param>
    public BookmarkIdentityDemo(
        IPluginConfigurationProvider configuration,
        ILibraryManager library,
        IdentityResolver resolver,
        MoveJournalReader moves,
        TimeProvider? timeProvider = null)
    {
        _configuration = configuration;
        _library = library;
        _resolver = resolver;
        _moves = moves;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public IDictionary<string, object?> Capture(Guid itemId, User? caller)
    {
        EnsureEnabled();
        if (itemId == Guid.Empty)
        {
            throw PluginException.Invalid("An item id is required.");
        }

        // "Not found" and "not visible" are the same 404 (the kit's privacy rule).
        var item = _library.GetItemById<BaseItem>(itemId, caller) ?? throw PluginException.NotFound("No library item has this id.");
        var snapshot = ItemIdentity.Capture(item, _library);
        return new Dictionary<string, object?>
        {
            ["Identity"] = JsonSerializer.SerializeToElement(snapshot, JsonStoreFormat.Options),
            ["StrongKeys"] = ItemIdentity.StrongKeys(snapshot)
        };
    }

    /// <inheritdoc />
    public IDictionary<string, object?> Resolve(JsonElement snapshot, string? policy, DateTime? attachedUtc, User? caller)
    {
        EnsureEnabled();
        var relinkPolicy = policy switch
        {
            null or "" or "Default" => RelinkPolicy.Default,
            "Bookmarks" => RelinkPolicy.RuntimeWithin(),
            "ExactOnly" => RelinkPolicy.ExactOnly,
            _ => throw PluginException.Invalid("The policy must be Default, Bookmarks or ExactOnly.")
        };

        IdentitySnapshot? identity;
        try
        {
            identity = snapshot.ValueKind == JsonValueKind.Object ? snapshot.Deserialize<IdentitySnapshot>(JsonStoreFormat.Options) : null;
        }
        catch (JsonException)
        {
            identity = null;
        }

        if (identity is null)
        {
            throw PluginException.Invalid("The body must be an identity snapshot.");
        }

        var view = new CallerView(_library, caller);
        var journal = _moves.Read();
        var result = _resolver.Resolve(identity, relinkPolicy, journal, attachedUtc, view.IsVisible);
        return new Dictionary<string, object?>
        {
            ["Outcome"] = result.Outcome.ToString(),
            ["Strength"] = result.Strength.ToString(),
            ["Source"] = result.Source.ToString(),
            ["ItemId"] = result.ItemId,
            ["ItemName"] = result.Item?.Name,
            ["ItemPath"] = result.Item?.Path,
            ["IsPending"] = result.IsPending,
            ["Reason"] = result.Reason.ToString(),
            ["Candidates"] = result.Candidates.Select(c => new Dictionary<string, object?>
            {
                ["ItemId"] = c.ItemId,
                ["Kind"] = ItemIdentity.KindOf(c.Item),
                ["Name"] = c.Item.Name,
                ["Year"] = c.Item.ProductionYear,
                ["RunTimeTicks"] = c.Item.RunTimeTicks,
                ["Path"] = c.Item.Path,
                ["Strength"] = c.Strength.ToString(),
                ["Source"] = c.Source.ToString(),
                ["PassesValidation"] = c.PassesValidation
            }).ToList(),
            ["Chain"] = result.Chain is { } chain
                ? new Dictionary<string, object?>
                {
                    ["StartId"] = chain.StartId,
                    ["EndId"] = view.Hides(chain.EndId) ? null : chain.EndId,
                    ["Revisits"] = chain.Revisits,
                    ["Steps"] = chain.Steps.Where(view.Shows).Select(view.Describe).ToList()
                }
                : null,
            ["JournalStatus"] = journal.Status.ToString()
        };
    }

    /// <inheritdoc />
    public IDictionary<string, object?> Journal(Guid? journalId, long lastMoveSeq, User? caller)
    {
        EnsureEnabled();
        if (lastMoveSeq < 0)
        {
            throw PluginException.Invalid("LastMoveSeq can't be negative.");
        }

        var view = new CallerView(_library, caller);
        var journal = _moves.Read();
        var catchUp = MoveJournalReader.CatchUp(journal, journalId, lastMoveSeq);
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        return new Dictionary<string, object?>
        {
            ["File"] = _moves.FilePath,
            ["Status"] = journal.Status.ToString(),
            ["JournalId"] = journal.JournalId,
            ["CreatedUtc"] = journal.CreatedUtc,
            ["LastSeq"] = journal.LastSeq,
            ["Lines"] = journal.Lines.Count,
            ["IgnoredLines"] = journal.IgnoredLines,
            ["InvalidLines"] = journal.InvalidLines,
            ["TruncatedTail"] = journal.TruncatedTail,
            ["Problem"] = journal.Problem,
            ["CatchUp"] = new Dictionary<string, object?>
            {
                ["Reset"] = catchUp.Reset.ToString(),
                ["JournalId"] = catchUp.JournalId,
                ["LastMoveSeq"] = catchUp.LastMoveSeq,
                ["Lines"] = catchUp.Lines.Where(view.Shows).Select(view.Describe).ToList()
            },
            ["PendingRelinks"] = journal.PendingRelinks(now).Where(view.Shows).Select(line => new Dictionary<string, object?>
            {
                ["NewId"] = line.NewId,
                ["Seq"] = line.Seq,
                ["Utc"] = line.Utc,
                ["InLibrary"] = view.Sees(line.NewId)
            }).ToList()
        };
    }

    private void EnsureEnabled()
    {
        if (!_configuration.GetConfiguration().EnableIdentityDemo)
        {
            throw PluginException.FeatureDisabled("The identity demo is turned off (developer setting EnableIdentityDemo).");
        }
    }

    /// <summary>
    /// What the caller may see (the caller's own library access): an API key everything, an admin the items
    /// <c>GetItemById&lt;BaseItem&gt;(id, admin)</c> finds. An id the library doesn't have names no item.
    /// </summary>
    private sealed class CallerView
    {
        private readonly ILibraryManager _library;

        public CallerView(ILibraryManager library, User? caller)
        {
            _library = library;
            IsVisible = caller is null ? null : IdentityResolver.VisibleTo(library, caller);
        }

        /// <summary>Gets the resolver's check; null for an API key.</summary>
        public Func<BaseItem, bool>? IsVisible { get; }

        /// <summary>Whether the library has the item and the caller can see it.</summary>
        public bool Sees(Guid id) => _library.GetItemById(id) is { } item && (IsVisible is null || IsVisible(item));

        /// <summary>Whether the id names an item the caller can't see.</summary>
        public bool Hides(Guid id) => IsVisible is { } isVisible && id != Guid.Empty && _library.GetItemById(id) is { } item && !isVisible(item);

        /// <summary>Whether a journal line may be shown: it names no item the caller can't see.</summary>
        public bool Shows(MoveJournalLine line) => !Hides(line.OldId) && !Hides(line.NewId);

        /// <summary>Describes a journal line; its <c>NewPath</c> only with an item the caller can see.</summary>
        public Dictionary<string, object?> Describe(MoveJournalLine line) => new()
        {
            ["Seq"] = line.Seq,
            ["Utc"] = line.Utc.ToString("O", CultureInfo.InvariantCulture),
            ["OldId"] = line.OldId,
            ["NewId"] = line.NewId,
            ["Kind"] = line.Kind,
            ["NewPath"] = IsVisible is null || Sees(line.NewId) ? line.NewPath : null,
            ["Correction"] = line.Correction
        };
    }
}
