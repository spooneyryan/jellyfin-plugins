using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.SmartSearch.Api.Models;
using Jellyfin.Plugin.SmartSearch.Query;
using Jellyfin.Plugin.SmartSearch.Query.Fields;
using Jellyfin.Plugin.SmartSearch.Query.Validation;
using Jellyfin.Plugin.SmartSearch.Storage;

namespace Jellyfin.Plugin.SmartSearch.AutoCollections;

/// <summary>
/// The rules a definition must pass, the same on save, on import, before <b>every</b> run, and for every edit of a
/// referenced saved query: a query can change after the definition was saved, a user can be deleted, and a normal
/// collection a rule names can become an auto-collection.
/// </summary>
/// <remarks>
/// <para>In order, each stopping the run with its status (no collection is written):</para>
/// <list type="number">
/// <item>Fail closed: a read-only store, or a definition with a field or enum value this version doesn't know →
/// <c>NeedsNewerVersion</c>.</item>
/// <item>The creator must exist (their libraries are the scope) → <c>CreatorMissing</c>.</item>
/// <item>The source: a referenced saved query must exist (<c>QueryMissing</c>) and still belong to the creator; a query
/// this version can't read, or a missing copy, is <c>QueryInvalid</c>.</item>
/// <item>The query must be valid and runnable <b>for the creator</b> (an administrator-only field only for an
/// administrator creator; the server's allowed kinds; every field available).</item>
/// <item>Its kinds must be videos (Movie, Series, Episode, MusicVideo, Video): a collection never holds collections.</item>
/// <item>No <c>InCollection</c> rule may name an auto-collection (cycles). <c>IsEmpty</c>/<c>IsNotEmpty</c> name none and
/// are allowed: the engine ignores auto-collections for them.</item>
/// <item><c>Limit</c> at most <c>MaxMembersPerCollection</c>.</item>
/// <item>Per-user rules need an evaluation user who exists (<c>EvaluationUserMissing</c>); without per-user rules a run
/// clears a leftover evaluation user, a save refuses one.</item>
/// </list>
/// <para>On save also: a non-administrator may only name themselves as the evaluation user. The name is checked by the
/// collection name checker.</para>
/// </remarks>
internal sealed class AutoCollectionChecks
{
    /// <summary>The field whose rules name collections.</summary>
    public const string InCollectionField = "InCollection";

    /// <summary>The kinds a collection may hold.</summary>
    public static readonly IReadOnlySet<BaseItemKind> MemberKinds = new HashSet<BaseItemKind> { BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Episode, BaseItemKind.MusicVideo, BaseItemKind.Video };

    private readonly QueryValidator _validator;
    private readonly QueryCanonicalizer _canonicalizer;
    private readonly IFieldCatalog _catalog;

    /// <summary>
    /// Initializes a new instance of the <see cref="AutoCollectionChecks"/> class.
    /// </summary>
    /// <param name="validator">The query validator.</param>
    /// <param name="canonicalizer">The canonicalizer (the query's hash).</param>
    /// <param name="catalog">The field catalog.</param>
    public AutoCollectionChecks(QueryValidator validator, QueryCanonicalizer canonicalizer, IFieldCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(canonicalizer);
        ArgumentNullException.ThrowIfNull(catalog);
        _validator = validator;
        _canonicalizer = canonicalizer;
        _catalog = catalog;
    }

    /// <summary>Checks a definition before a run.</summary>
    /// <param name="definition">The definition.</param>
    /// <param name="context">What the checks read.</param>
    /// <returns>The query to run, or why not.</returns>
    public AutoCollectionCheck CheckForRun(AutoCollectionDefinition definition, AutoCollectionCheckContext context)
        => Check(definition, context, actor: null, actorIsAdmin: false, keptEvaluationUser: null);

    /// <summary>Checks a definition a user saves (create, edit, import).</summary>
    /// <param name="definition">The definition as it would be stored.</param>
    /// <param name="context">What the checks read.</param>
    /// <param name="actor">Who saves.</param>
    /// <param name="actorIsAdmin">Whether they pass <c>RequiresElevation</c> on this request.</param>
    /// <param name="keptEvaluationUser">The evaluation user the stored definition already has, which an edit may keep; <c>null</c> for a new one.</param>
    /// <returns>The query, or why the definition can't be saved.</returns>
    public AutoCollectionCheck CheckForSave(AutoCollectionDefinition definition, AutoCollectionCheckContext context, User actor, bool actorIsAdmin, Guid? keptEvaluationUser = null)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return Check(definition, context, actor, actorIsAdmin, keptEvaluationUser);
    }

    /// <summary>
    /// Tells whether two sources are the same: the same saved query, or copies of queries with the same meaning (the same
    /// canonical hash). A source sent back as it was read is no change.
    /// </summary>
    /// <param name="stored">The source kept.</param>
    /// <param name="asked">The source asked for.</param>
    /// <returns>Whether they are the same.</returns>
    public bool SameSource(AutoCollectionSource? stored, AutoCollectionSource? asked)
    {
        if (stored is null || asked is null || stored.Mode.Value != asked.Mode.Value)
        {
            return false;
        }

        if (stored.Mode.Value == SmartSearchAutoCollectionSourceMode.Reference)
        {
            return stored.SavedQueryId is { } id && id.Equals(asked.SavedQueryId);
        }

        return HashOf(stored.Snapshot) is { } hash && string.Equals(hash, HashOf(asked.Snapshot), StringComparison.Ordinal);
    }

    /// <summary>Finds the collection ids <c>InCollection</c> rules name.</summary>
    /// <param name="query">The query.</param>
    /// <returns>The ids.</returns>
    public IReadOnlySet<Guid> CollectionsNamed(SmartSearchQueryDefinition query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var ids = new HashSet<Guid>();
        Walk(query.Where, ids);
        return ids;
    }

    private static AutoCollectionCheck Stop(SmartSearchAutoCollectionStatus status, string reason, string message, User? creator = null)
        => new(status, reason, [message], null, null, false, creator, null, false);

    private static Guid? ReadId(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var id))
        {
            return id;
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                if (string.Equals(property.Name, "Id", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String && Guid.TryParse(property.Value.GetString(), out var objectId))
                {
                    return objectId;
                }
            }
        }

        return null;
    }

    private AutoCollectionCheck Check(AutoCollectionDefinition definition, AutoCollectionCheckContext context, User? actor, bool actorIsAdmin, Guid? keptEvaluationUser)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(context);

        // 1. Fail closed.
        if (context.StoreReadOnly)
        {
            return Stop(SmartSearchAutoCollectionStatus.NeedsNewerVersion, AutoCollectionReasons.StoreReadOnly, "The auto-collections file can't be written by this version of Smart Search.");
        }

        if (definition.FromNewerVersion() is { } newer)
        {
            return Stop(SmartSearchAutoCollectionStatus.NeedsNewerVersion, AutoCollectionReasons.NewerDefinition, "A newer version of Smart Search wrote this auto-collection (" + newer + ").");
        }

        // 2. The creator.
        var creator = context.FindUser(definition.CreatedByUserId);
        if (creator is null)
        {
            return Stop(SmartSearchAutoCollectionStatus.CreatorMissing, AutoCollectionReasons.CreatorDeleted, "The user who made this auto-collection was deleted; an administrator can give it to another user.");
        }

        var creatorIsAdmin = actor is not null && actor.Id.Equals(creator.Id) ? actorIsAdmin : CurrentUser.IsAdmin(creator);

        // 3. The source.
        var source = ResolveSource(definition, context.SavedQueries);
        if (source.Status != SmartSearchAutoCollectionStatus.Ok)
        {
            return Stop(source.Status, source.Reason!, source.Message!, creator);
        }

        var query = source.Query!;

        // 4. Valid and runnable for the creator.
        var validation = _validator.Validate(query, new QueryValidationContext(creatorIsAdmin, false, context.AllowedItemKinds));
        if (!validation.Supported)
        {
            return Stop(SmartSearchAutoCollectionStatus.QueryInvalid, AutoCollectionReasons.QueryUnsupported, "The query was saved by a newer version of Smart Search.", creator);
        }

        if (!validation.Valid)
        {
            var adminOnly = validation.Errors.Any(e => e.Code == QueryErrorCodes.AdminOnlyField);
            return new AutoCollectionCheck(
                SmartSearchAutoCollectionStatus.QueryInvalid,
                adminOnly ? AutoCollectionReasons.AdminOnlyField : AutoCollectionReasons.InvalidQuery,
                validation.Errors.Select(e => (string.IsNullOrEmpty(e.Path) ? string.Empty : e.Path + ": ") + e.Message).ToList(),
                null,
                null,
                validation.UsesPerUserFields,
                creator,
                null,
                false);
        }

        if (validation.UnavailableFields.Count > 0)
        {
            return Stop(SmartSearchAutoCollectionStatus.QueryInvalid, AutoCollectionReasons.FieldUnavailable, "The query uses fields this server can't answer now: " + string.Join(", ", validation.UnavailableFields) + ".", creator);
        }

        // 5. Kinds.
        if (query.Scope.ItemKinds.Contains(BaseItemKind.BoxSet))
        {
            return Stop(SmartSearchAutoCollectionStatus.QueryInvalid, AutoCollectionReasons.BoxSetKind, "A collection can't hold collections: take Collections out of the query's item kinds.", creator);
        }

        if (query.Scope.ItemKinds.Any(k => !MemberKinds.Contains(k)))
        {
            return Stop(SmartSearchAutoCollectionStatus.QueryInvalid, AutoCollectionReasons.KindNotAllowed, "A collection holds movies, shows, episodes, music videos and videos only.", creator);
        }

        // 6. Cycles.
        if (CollectionsNamed(query).Any(context.AutoCollectionIds.Contains))
        {
            return Stop(SmartSearchAutoCollectionStatus.QueryInvalid, AutoCollectionReasons.CycleWithAutoCollection, "An In collection rule names an auto-collection; auto-collections can't depend on each other.", creator);
        }

        // 7. The limit.
        if (query.Limit is { } limit && limit > context.MaxMembersPerCollection)
        {
            return Stop(SmartSearchAutoCollectionStatus.QueryInvalid, AutoCollectionReasons.LimitAboveCap, "The query's limit is above the most members a collection may have (" + context.MaxMembersPerCollection + ").", creator);
        }

        // 8. The evaluation user.
        User? evaluationUser = null;
        var clear = false;
        if (validation.UsesPerUserFields)
        {
            if (definition.EvaluationUserId is not { } evaluationId || evaluationId == Guid.Empty)
            {
                return Stop(SmartSearchAutoCollectionStatus.EvaluationUserMissing, AutoCollectionReasons.EvaluationUserRequired, "The query reads watch state: choose whose.", creator);
            }

            evaluationUser = context.FindUser(evaluationId);
            if (evaluationUser is null)
            {
                return Stop(SmartSearchAutoCollectionStatus.EvaluationUserMissing, AutoCollectionReasons.EvaluationUserDeleted, "The evaluation user was deleted: choose another.", creator);
            }

            if (actor is not null && !actorIsAdmin && !evaluationId.Equals(actor.Id) && evaluationId != keptEvaluationUser)
            {
                return Stop(SmartSearchAutoCollectionStatus.QueryInvalid, AutoCollectionReasons.EvaluationUserNotAllowed, "Only an administrator can choose another user's watch state.", creator);
            }
        }
        else if (definition.EvaluationUserId is not null)
        {
            if (actor is not null)
            {
                return Stop(SmartSearchAutoCollectionStatus.QueryInvalid, AutoCollectionReasons.EvaluationUserNotNeeded, "The query doesn't read watch state, so it takes no evaluation user.", creator);
            }

            clear = true;
        }

        return new AutoCollectionCheck(SmartSearchAutoCollectionStatus.Ok, null, [], query, _canonicalizer.Canonicalize(query).Hash, validation.UsesPerUserFields, creator, evaluationUser, clear);
    }

    // The definition's query: a referenced saved query of the creator's, or the copy.
    private static SourceResolution ResolveSource(AutoCollectionDefinition definition, SavedQueriesDocument savedQueries)
    {
        var source = definition.Source;
        if (source is null)
        {
            return SourceResolution.Fail(SmartSearchAutoCollectionStatus.QueryInvalid, AutoCollectionReasons.SourceMissing, "The auto-collection has no source query.");
        }

        JsonElement json;
        if (source.Mode.Value == SmartSearchAutoCollectionSourceMode.Reference)
        {
            var record = source.SavedQueryId is { } id ? (savedQueries.Queries ?? []).FirstOrDefault(q => q is not null && q.Id.Equals(id)) : null;
            if (record is null)
            {
                return SourceResolution.Fail(SmartSearchAutoCollectionStatus.QueryMissing, AutoCollectionReasons.QueryMissing, "The saved query is gone.");
            }

            if (!record.OwnerUserId.Equals(definition.CreatedByUserId))
            {
                return SourceResolution.Fail(SmartSearchAutoCollectionStatus.QueryInvalid, AutoCollectionReasons.SourceOwnerChanged, "The source query now belongs to someone else.");
            }

            json = record.Query;
        }
        else if (source.Snapshot is { } snapshot)
        {
            json = snapshot;
        }
        else
        {
            return SourceResolution.Fail(SmartSearchAutoCollectionStatus.QueryInvalid, AutoCollectionReasons.SourceMissing, "The auto-collection's copy of its query is missing.");
        }

        if (json.ValueKind != JsonValueKind.Object)
        {
            return SourceResolution.Fail(SmartSearchAutoCollectionStatus.QueryInvalid, AutoCollectionReasons.InvalidQuery, "The query is not a JSON object.");
        }

        var read = QueryJson.Read(System.Text.Encoding.UTF8.GetBytes(json.GetRawText()));
        if (read.Definition is null)
        {
            return read.Supported
                ? SourceResolution.Fail(SmartSearchAutoCollectionStatus.QueryInvalid, AutoCollectionReasons.InvalidQuery, "The query can't be read.")
                : SourceResolution.Fail(SmartSearchAutoCollectionStatus.QueryInvalid, AutoCollectionReasons.QueryUnsupported, "The query was saved by a newer version of Smart Search.");
        }

        return new SourceResolution(SmartSearchAutoCollectionStatus.Ok, null, null, read.Definition);
    }

    /// <summary>The canonical hash of a stored query (what it means, not how it is spelled), or <c>null</c> when it can't be read.</summary>
    /// <param name="json">The query as stored.</param>
    /// <returns>The hash, or <c>null</c>.</returns>
    public string? HashOf(JsonElement? json)
        => json is { ValueKind: JsonValueKind.Object } query && QueryJson.Read(System.Text.Encoding.UTF8.GetBytes(query.GetRawText())).Definition is { } read
            ? _canonicalizer.Canonicalize(read).Hash
            : null;

    private void Walk(SmartSearchQueryNode? node, HashSet<Guid> ids)
    {
        if (node is null)
        {
            return;
        }

        if (node.Children is { } children)
        {
            foreach (var child in children)
            {
                Walk(child, ids);
            }

            return;
        }

        if (node.Field is null || !string.Equals(_catalog.Find(node.Field)?.Id ?? node.Field, InCollectionField, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        foreach (var value in (node.Values ?? []).Concat(node.Value is { } one ? [one] : []))
        {
            if (ReadId(value) is { } id)
            {
                ids.Add(id);
            }
        }
    }

    private sealed record SourceResolution(SmartSearchAutoCollectionStatus Status, string? Reason, string? Message, SmartSearchQueryDefinition? Query)
    {
        public static SourceResolution Fail(SmartSearchAutoCollectionStatus status, string reason, string message) => new(status, reason, message, null);
    }
}
