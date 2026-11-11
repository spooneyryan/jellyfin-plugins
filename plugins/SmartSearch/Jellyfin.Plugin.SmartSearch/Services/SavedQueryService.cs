using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Users;
using Jellyfin.Plugin.SmartSearch.Api.Models;
using Jellyfin.Plugin.SmartSearch.Query;
using Jellyfin.Plugin.SmartSearch.Query.Validation;
using Jellyfin.Plugin.SmartSearch.Storage;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartSearch.Services;

/// <summary>
/// The saved-query endpoints for the signed-in user (<see cref="ISmartSearchSavedQueries"/>).
/// </summary>
/// <remarks>
/// <para><b>Who sees what.</b> A user opens their own queries and the shared queries of others; an administrator any
/// query by id. A private query of someone else is a 404, as an id that doesn't exist. While sharing is turned off on the
/// server (<c>AllowQuerySharing</c>), shared queries are nobody's but their owner's, and a shared query that uses an
/// administrator-only field, or that this version can't read, is never another user's to open. Owners are named by their
/// user name; a deleted owner is "Deleted user". Only administrators get the owner's id (<c>IsOwner</c> tells a user
/// their own queries).</para>
/// <para><b>Running.</b> A query is run as the caller through <see cref="ISmartSearchService"/>, the same path as
/// <c>/Search</c>: the caller's libraries, parental limit and watch state, and the caller's right to administrator-only
/// fields and the allowed kinds; never the owner's.</para>
/// <para><b>Writes</b> check the name, the quota and <c>IfUpdatedAt</c> (compared as UTC ticks) under the store's writer
/// lock, so two requests can't both take a name or the last place. A duplicate is a new query: it drops the original's
/// fields of a newer version (<c>Extra</c>). A change and a delete go through <see cref="ISavedQueryReferenceGuard"/>.</para>
/// </remarks>
internal sealed class SavedQueryService : ISmartSearchSavedQueries
{
    /// <summary>The owner name of a query whose owner was deleted.</summary>
    public const string DeletedUserName = "Deleted user";

    /// <summary>The largest page of a list.</summary>
    public const int MaxListLimit = 500;

    private static readonly TimeSpan LastRunInterval = TimeSpan.FromHours(1);

    private readonly SavedQueryStore _store;
    private readonly SavedQueryReader _reader;
    private readonly Func<ISavedQueryReferenceGuard> _guard;
    private readonly QueryValidator _validator;
    private readonly ISmartSearchService _search;
    private readonly IUserManager _users;
    private readonly Func<SavedQuerySettings> _settings;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SavedQueryService"/> class.
    /// </summary>
    /// <param name="store">The store.</param>
    /// <param name="reader">Reads stored queries.</param>
    /// <param name="guard">The reference guard.</param>
    /// <param name="validator">The validator.</param>
    /// <param name="search">The search, which runs a query as the caller.</param>
    /// <param name="users">The user manager (owner names).</param>
    /// <param name="settings">The settings, read on each request.</param>
    /// <param name="clock">The clock.</param>
    /// <param name="logger">The logger.</param>
    public SavedQueryService(
        SavedQueryStore store,
        SavedQueryReader reader,
        Func<ISavedQueryReferenceGuard> guard,
        QueryValidator validator,
        ISmartSearchService search,
        IUserManager users,
        Func<SavedQuerySettings> settings,
        TimeProvider clock,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _reader = reader;
        _guard = guard;
        _validator = validator;
        _search = search;
        _users = users;
        _settings = settings;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Which queries a list holds.</summary>
    private enum ListScope
    {
        Mine,
        Shared,
        All
    }

    /// <inheritdoc />
    public SmartSearchSavedQueryListDto List(User user, bool isAdmin, string? scope, int startIndex, int limit)
    {
        ArgumentNullException.ThrowIfNull(user);
        var settings = Gate();
        var which = ParseScope(scope);
        CheckPage(startIndex, limit);
        var owners = new OwnerNames(_users);
        var listed = _store.Current.Queries
            .Where(q =>
            {
                var mine = q.OwnerUserId.Equals(user.Id);
                return which switch
                {
                    ListScope.Mine => mine,
                    ListScope.Shared => !mine && SharedWithOthers(q, settings),
                    _ => mine || SharedWithOthers(q, settings)
                };
            })
            .OrderBy(q => q.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(q => q.Id)
            .ToList();
        return new SmartSearchSavedQueryListDto
        {
            Items = listed.Skip(startIndex).Take(limit).Select(q => Summary(q, user.Id, isAdmin, owners)).ToList(),
            TotalRecordCount = listed.Count,
            StartIndex = startIndex
        };
    }

    /// <inheritdoc />
    public SmartSearchSavedQueryDto Get(User user, bool isAdmin, Guid id)
    {
        ArgumentNullException.ThrowIfNull(user);
        var settings = Gate();
        return Full(Readable(id, user, isAdmin, settings), user.Id, isAdmin, new OwnerNames(_users), []);
    }

    /// <inheritdoc />
    public async Task<SmartSearchSavedQueryDto> CreateAsync(User user, bool isAdmin, SmartSearchSavedQueryRequestDto request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(request);
        var settings = Gate();
        var (name, description) = CheckText(request);
        var (definition, facts) = CheckQuery(request, isAdmin);
        CheckSharing(request.Shared, wasShared: false, facts, settings);

        var now = Now();
        var record = new SavedQueryRecord
        {
            Id = Guid.NewGuid(),
            OwnerUserId = user.Id,
            Name = name,
            Description = description,
            Shared = request.Shared,
            Query = ToElement(definition),
            QueryHash = facts.Hash,
            CreatedUtc = now,
            UpdatedUtc = now,
            UpdatedByUserId = user.Id
        };
        await _store.UpdateAsync(
            document =>
            {
                var owned = document.Queries.Where(q => q.OwnerUserId.Equals(user.Id)).ToList();
                CheckQuota(owned.Count, settings);
                CheckNameFree(owned, name, except: null);
                return SavedQueryStore.WithQueries(document, [.. document.Queries, record]) with
                {
                    KnownUsers = (document.KnownUsers ?? KnownUsers.Empty).With(user)
                };
            },
            cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Saved query {QueryId} created ({Hash})", record.Id, record.QueryHash);
        return Full(record, user.Id, isAdmin, new OwnerNames(_users), []);
    }

    /// <inheritdoc />
    public async Task<SmartSearchSavedQueryDto> UpdateAsync(User user, bool isAdmin, Guid id, SmartSearchSavedQueryRequestDto request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(request);
        var settings = Gate();
        var current = Writable(id, user, isAdmin, settings);
        if (!_reader.Read(current).Supported)
        {
            throw SavedQueryErrors.NeedsNewerVersion;
        }

        var (name, description) = CheckText(request);
        var (definition, facts) = CheckQuery(request, isAdmin);
        if (facts.UsesAdminOnlyFields && !current.OwnerUserId.Equals(user.Id))
        {
            // An administrator-only field only in an administrator's own query: its owner couldn't run it otherwise.
            throw new PluginException(PluginErrorKind.Forbidden, "An administrator-only field can only be used in your own queries.") { Code = QueryErrorCodes.AdminOnlyField };
        }

        CheckSharing(request.Shared, current.Shared, facts, settings);
        var ifUpdatedAt = request.IfUpdatedAt ?? throw SavedQueryErrors.Invalid(nameof(request.IfUpdatedAt), QueryErrorCodes.Required, "A change needs IfUpdatedAt.");
        var query = ToElement(definition);

        // Checked before the guard too, and by the guard under its lock just before it detaches the auto-collections, so a
        // refused save detaches nothing; the save checks both again under the store's lock.
        CheckSaveable(_store.Current, id, ifUpdatedAt, name);
        SavedQueryRecord? saved = null;
        var outcome = await _guard().UpdateAsync(
            new SavedQueryEdit(current, definition, facts.UsesPerUserFields, user, isAdmin, request.AcknowledgeAutoCollectionWarnings)
            {
                QueryJson = query,
                IfUpdatedAt = ifUpdatedAt,
                DetachReferences = request.DetachAutoCollections,
                CheckSave = document => CheckSaveable(document, id, ifUpdatedAt, name)
            },
            async ct => await _store.UpdateAsync(
                document =>
                {
                    var stored = CheckSaveable(document, id, ifUpdatedAt, name);
                    saved = stored with
                    {
                        Name = name,
                        Description = description,
                        Shared = request.Shared,
                        Query = query,
                        QueryHash = facts.Hash,
                        UpdatedUtc = Later(stored.UpdatedUtc),
                        UpdatedByUserId = user.Id
                    };
                    return SavedQueryStore.WithQueries(document, document.Queries.Select(q => q.Id.Equals(id) ? saved : q));
                },
                ct).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
        if (SavedQueryErrors.Of(outcome) is { } refused)
        {
            throw refused;
        }

        _logger.LogInformation("Saved query {QueryId} changed ({Hash})", id, facts.Hash);
        return Full(saved!, user.Id, isAdmin, new OwnerNames(_users), outcome.References);
    }

    /// <inheritdoc />
    public async Task<SmartSearchSavedQueryDto> DuplicateAsync(User user, bool isAdmin, Guid id, SmartSearchSavedQueryDuplicateDto request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(request);
        var settings = Gate();
        var original = Readable(id, user, isAdmin, settings);
        if (_reader.Read(original).UsesAdminOnlyFields && !isAdmin)
        {
            throw new PluginException(PluginErrorKind.Forbidden, "Only administrators can use this field.") { Code = QueryErrorCodes.AdminOnlyField };
        }

        var wanted = request.Name is null ? null : CheckName(request.Name);
        var now = Now();
        SavedQueryRecord? copy = null;
        await _store.UpdateAsync(
            document =>
            {
                var owned = document.Queries.Where(q => q.OwnerUserId.Equals(user.Id)).ToList();
                CheckQuota(owned.Count, settings);
                string name;
                if (wanted is null)
                {
                    name = SavedQueryNames.Free(original.Name, owned.Select(q => q.Name).ToHashSet(SavedQueryNames.Comparer));
                }
                else
                {
                    CheckNameFree(owned, wanted, except: null);
                    name = wanted;
                }

                copy = original with
                {
                    Id = Guid.NewGuid(),
                    OwnerUserId = user.Id,
                    Name = name,
                    Shared = false,
                    Query = original.Query.Clone(),
                    CreatedUtc = now,
                    UpdatedUtc = now,
                    UpdatedByUserId = user.Id,
                    LastRunUtc = null,
                    Extra = null // fields of a newer version are the original's, not the copy's
                };
                return SavedQueryStore.WithQueries(document, [.. document.Queries, copy]) with
                {
                    KnownUsers = (document.KnownUsers ?? KnownUsers.Empty).With(user)
                };
            },
            cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Saved query {QueryId} duplicated as {CopyId}", id, copy!.Id);
        return Full(copy, user.Id, isAdmin, new OwnerNames(_users), []);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(User user, bool isAdmin, Guid id, bool detachAutoCollections, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        var settings = Gate();
        var current = Writable(id, user, isAdmin, settings);
        var outcome = await _guard().DeleteAsync(
            new SavedQueryDeletion(current, user, isAdmin, detachAutoCollections),
            async ct => await _store.UpdateAsync(
                document => document.Queries.Any(q => q.Id.Equals(id))
                    ? SavedQueryStore.WithQueries(document, document.Queries.Where(q => !q.Id.Equals(id)))
                    : document,
                ct).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
        if (SavedQueryErrors.Of(outcome) is { } refused)
        {
            throw refused;
        }

        _logger.LogInformation("Saved query {QueryId} deleted", id);
    }

    /// <inheritdoc />
    public async Task<SmartSearchResultDto> RunAsync(User user, bool isAdmin, Guid id, SmartSearchSavedQueryRunDto request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(request);
        var settings = Gate();
        var record = Readable(id, user, isAdmin, settings);
        if (!_reader.Read(record).Supported)
        {
            throw SavedQueryErrors.NeedsNewerVersion;
        }

        // A fresh read of the stored JSON: the search checks it for the caller, as it would a query sent to /Search.
        var read = QueryJson.Read(Encoding.UTF8.GetBytes(record.Query.GetRawText()));
        var page = await _search.SearchAsync(
            user,
            isAdmin,
            new SmartSearchRequestDto
            {
                Read = read,
                Query = read.Definition,
                StartIndex = request.StartIndex,
                Limit = request.Limit,
                ResultToken = request.ResultToken,
                IncludeTotal = request.IncludeTotal
            },
            cancellationToken).ConfigureAwait(false);
        if (record.OwnerUserId.Equals(user.Id))
        {
            await NoteRunAsync(id, cancellationToken).ConfigureAwait(false);
        }

        return page;
    }

    private static ListScope ParseScope(string? scope)
    {
        var text = scope?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return ListScope.All;
        }

        foreach (var candidate in Enum.GetValues<ListScope>())
        {
            if (string.Equals(candidate.ToString(), text, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        throw SavedQueryErrors.Invalid("scope", QueryErrorCodes.InvalidValue, "scope must be Mine, Shared or All.");
    }

    private static void CheckPage(int startIndex, int limit)
    {
        if (startIndex < 0)
        {
            throw SavedQueryErrors.Invalid("startIndex", QueryErrorCodes.OutOfRange, "startIndex must be 0 or more.");
        }

        if (limit is < 1 or > MaxListLimit)
        {
            throw SavedQueryErrors.Invalid("limit", QueryErrorCodes.OutOfRange, "limit must be from 1 to 500.");
        }
    }

    private static string CheckName(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length is 0 or > SavedQueryNames.MaxNameLength)
        {
            throw SavedQueryErrors.Invalid("Name", QueryErrorCodes.OutOfRange, "The name must be 1 to 100 characters.");
        }

        return trimmed;
    }

    private static (string Name, string Description) CheckText(SmartSearchSavedQueryRequestDto request)
    {
        var name = CheckName(request.Name ?? string.Empty);
        var description = request.Description ?? string.Empty;
        if (description.Length > SavedQueryNames.MaxDescriptionLength)
        {
            throw SavedQueryErrors.Invalid("Description", QueryErrorCodes.OutOfRange, "The description must be at most 500 characters.");
        }

        return (name, description);
    }

    private static void CheckQuota(int owned, SavedQuerySettings settings)
    {
        if (owned >= settings.MaxPerUser)
        {
            throw new PluginException(PluginErrorKind.Conflict, "You have as many saved queries as this server allows (" + settings.MaxPerUser + "). Delete one first.") { Code = SavedQueryErrors.QuotaReachedCode };
        }
    }

    private static void CheckNameFree(IEnumerable<SavedQueryRecord> owned, string name, Guid? except)
    {
        if (owned.Any(q => !q.Id.Equals(except) && SavedQueryNames.Comparer.Equals(q.Name, name)))
        {
            throw new PluginException(PluginErrorKind.Conflict, "There is already a saved query of this name.") { Code = SavedQueryErrors.DuplicateNameCode };
        }
    }

    private static void CheckSharing(bool shared, bool wasShared, SavedQueryFacts facts, SavedQuerySettings settings)
    {
        if (!shared)
        {
            return;
        }

        if (facts.UsesAdminOnlyFields)
        {
            throw new PluginException(PluginErrorKind.Forbidden, "A query that uses an administrator-only field can't be shared.") { Code = SavedQueryErrors.SharedAdminOnlyFieldCode };
        }

        if (!wasShared && !settings.AllowSharing)
        {
            throw new PluginException(PluginErrorKind.Forbidden, "Sharing queries is turned off on this server.") { Code = SavedQueryErrors.SharingDisabledCode };
        }
    }

    // The stored query a change applies to: still there, not changed since the caller read it, and the new name free
    // among its owner's other queries.
    private static SavedQueryRecord CheckSaveable(SavedQueriesDocument document, Guid id, DateTime ifUpdatedAt, string name)
    {
        var stored = document.Queries.FirstOrDefault(q => q.Id.Equals(id)) ?? throw SavedQueryErrors.NotFound;
        if (UtcDateTimeConverter.Utc(stored.UpdatedUtc).Ticks != UtcDateTimeConverter.Utc(ifUpdatedAt).Ticks)
        {
            throw new PluginException(PluginErrorKind.Conflict, "The query was changed elsewhere since you opened it. Reload it, or save yours as a new query.") { Code = SavedQueryErrors.ChangedCode };
        }

        CheckNameFree(document.Queries.Where(q => q.OwnerUserId.Equals(stored.OwnerUserId)), name, except: id);
        return stored;
    }

    private static JsonElement ToElement(SmartSearchQueryDefinition definition)
    {
        using var document = JsonDocument.Parse(QueryJson.Write(definition));
        return document.RootElement.Clone();
    }

    // Shared with users other than the owner: sharing on, a query this version reads (one it can't read may hold anything,
    // an administrator-only field included), and no administrator-only field in it.
    private bool SharedWithOthers(SavedQueryRecord query, SavedQuerySettings settings)
    {
        if (!query.Shared || !settings.AllowSharing)
        {
            return false;
        }

        var facts = _reader.Read(query);
        return facts.Supported && facts.Definition is not null && !facts.UsesAdminOnlyFields;
    }

    private SavedQuerySettings Gate()
    {
        var settings = _settings();
        SearchPageGate.Ensure(settings.Enabled);
        return settings;
    }

    // A query the caller may open: their own, one shared with others, or any for an administrator; else 404.
    private SavedQueryRecord Readable(Guid id, User user, bool isAdmin, SavedQuerySettings settings)
    {
        var query = _store.Find(id);
        return query is not null && (isAdmin || query.OwnerUserId.Equals(user.Id) || SharedWithOthers(query, settings))
            ? query
            : throw SavedQueryErrors.NotFound;
    }

    // A query the caller may change: their own, or any for an administrator; 403 for one shared with them, else 404.
    private SavedQueryRecord Writable(Guid id, User user, bool isAdmin, SavedQuerySettings settings)
    {
        var query = Readable(id, user, isAdmin, settings);
        return isAdmin || query.OwnerUserId.Equals(user.Id) ? query : throw SavedQueryErrors.NotOwner;
    }

    private (SmartSearchQueryDefinition Definition, SavedQueryFacts Facts) CheckQuery(SmartSearchSavedQueryRequestDto request, bool isAdmin)
    {
        var read = request.Read ?? throw SavedQueryErrors.Invalid("Query", QueryErrorCodes.Required, "The request has no query.");
        if (read.Definition is null)
        {
            throw SmartSearchService.QueryErrors(read.Errors);
        }

        var validation = _validator.Validate(read.Definition, new QueryValidationContext(isAdmin, EnableRegex: false, _settings().AllowedKinds));
        if (!validation.Valid)
        {
            throw SmartSearchService.QueryErrors(validation.Errors);
        }

        // Saved as it will be read back: the facts of the stored form (its hash, what it uses).
        return (read.Definition, _reader.Read(read.Definition));
    }

    private async Task NoteRunAsync(Guid id, CancellationToken cancellationToken)
    {
        var now = Now();
        if (_store.Find(id) is not { } stored || (stored.LastRunUtc is { } last && now - last < LastRunInterval) || _store.IsReadOnly)
        {
            return;
        }

        try
        {
            await _store.UpdateAsync(
                document =>
                {
                    var query = document.Queries.FirstOrDefault(q => q.Id.Equals(id));
                    return query is null || (query.LastRunUtc is { } at && now - at < LastRunInterval)
                        ? document
                        : SavedQueryStore.WithQueries(document, document.Queries.Select(q => q.Id.Equals(id) ? q with { LastRunUtc = now } : q));
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (PluginException ex)
        {
            // Only a note: a store that can't be written now doesn't fail the search.
            _logger.LogDebug(ex, "The last run of saved query {QueryId} wasn't written", id);
        }
    }

    // Times to the millisecond, so the UpdatedUtc a client reads back compares equal as IfUpdatedAt.
    private DateTime Now()
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        return new DateTime(now.Ticks - (now.Ticks % TimeSpan.TicksPerMillisecond), DateTimeKind.Utc);
    }

    // A change's time: now, and always after the one it replaces, so an IfUpdatedAt read before it never matches after it.
    private DateTime Later(DateTime previous)
    {
        var now = Now();
        return now > previous ? now : previous.AddMilliseconds(1);
    }

    private SmartSearchSavedQuerySummaryDto Summary(SavedQueryRecord query, Guid callerId, bool isAdmin, OwnerNames owners)
    {
        var facts = _reader.Read(query);
        var owner = owners.Find(query.OwnerUserId);
        return new SmartSearchSavedQuerySummaryDto
        {
            Id = query.Id,
            Name = query.Name,
            Description = query.Description,
            OwnerUserId = isAdmin ? query.OwnerUserId : null,
            OwnerName = owner?.Username ?? DeletedUserName,
            OwnerDeleted = owner is null,
            IsOwner = query.OwnerUserId.Equals(callerId),
            Shared = query.Shared,
            UpdatedUtc = query.UpdatedUtc,
            Supported = facts.Supported,
            Valid = facts.Valid,

            // Which auto-collections use a query is its owner's and the administrators' business, not every viewer's.
            AutoCollectionCount = isAdmin || query.OwnerUserId.Equals(callerId) ? _guard().CountReferences(query.Id) : 0
        };
    }

    private SmartSearchSavedQueryDto Full(SavedQueryRecord query, Guid callerId, bool isAdmin, OwnerNames owners, IReadOnlyList<SmartSearchAutoCollectionRefDto> affected)
        => ToDto(query, _reader.Read(query), owners.Find(query.OwnerUserId), callerId, isAdmin, _guard().CountReferences(query.Id), affected);

    /// <summary>Builds the full DTO of a query.</summary>
    /// <param name="query">The query.</param>
    /// <param name="facts">Its facts.</param>
    /// <param name="owner">Its owner, or <c>null</c> when deleted.</param>
    /// <param name="callerId">The caller's id (empty for an API key).</param>
    /// <param name="isAdmin">Whether the caller is an administrator.</param>
    /// <param name="references">What references it.</param>
    /// <param name="affected">The auto-collections a change affected.</param>
    /// <returns>The DTO.</returns>
    internal static SmartSearchSavedQueryDto ToDto(SavedQueryRecord query, SavedQueryFacts facts, User? owner, Guid callerId, bool isAdmin, int references, IReadOnlyList<SmartSearchAutoCollectionRefDto> affected)
    {
        var isOwner = query.OwnerUserId.Equals(callerId);
        return new SmartSearchSavedQueryDto
        {
            Id = query.Id,
            Name = query.Name,
            Description = query.Description,
            OwnerUserId = isAdmin ? query.OwnerUserId : null,
            OwnerName = owner?.Username ?? DeletedUserName,
            OwnerDeleted = owner is null,
            IsOwner = isOwner,
            CanEdit = isOwner || isAdmin,
            Shared = query.Shared,
            Query = query.Query,
            QueryHash = facts.Hash,
            Supported = facts.Supported,
            Valid = facts.Valid,
            Errors = SmartSearchService.Dtos(facts.Errors),
            UsesPerUserFields = facts.UsesPerUserFields,
            UsesAdminOnlyFields = facts.UsesAdminOnlyFields,
            CreatedUtc = query.CreatedUtc,
            UpdatedUtc = query.UpdatedUtc,
            LastRunUtc = isOwner || isAdmin ? query.LastRunUtc : null,
            AutoCollectionCount = isOwner || isAdmin ? references : 0,
            AffectedAutoCollections = affected
        };
    }

    /// <summary>The users of one request, each looked up once.</summary>
    internal sealed class OwnerNames
    {
        private readonly IUserManager _users;
        private readonly Dictionary<Guid, User?> _found = [];

        /// <summary>
        /// Initializes a new instance of the <see cref="OwnerNames"/> class.
        /// </summary>
        /// <param name="users">The user manager.</param>
        public OwnerNames(IUserManager users)
        {
            _users = users;
        }

        /// <summary>Finds a user.</summary>
        /// <param name="id">The id.</param>
        /// <returns>The user, or <c>null</c> when the server doesn't have it.</returns>
        public User? Find(Guid id)
        {
            if (!_found.TryGetValue(id, out var user))
            {
                _found[id] = user = id.Equals(Guid.Empty) ? null : _users.GetUserById(id);
            }

            return user;
        }
    }
}
