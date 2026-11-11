using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.SmartSearch.Api;
using Jellyfin.Plugin.SmartSearch.Api.Models;
using Jellyfin.Plugin.SmartSearch.Query.Validation;
using Jellyfin.Plugin.SmartSearch.Storage;

namespace Jellyfin.Plugin.SmartSearch.Services;

/// <summary>
/// The refusals of the saved-query endpoints, each with a stable <c>code</c>. Lists in a problem (<c>errors</c>,
/// <c>autoCollections</c>) are camelCase like the problem's own members.
/// </summary>
internal static class SavedQueryErrors
{
    /// <summary>404: no query of this id, or a private one the caller can't open (never told apart).</summary>
    public const string NotFoundCode = "QueryNotFound";

    /// <summary>403: a shared query the caller can open but isn't theirs to change.</summary>
    public const string NotOwnerCode = "NotOwner";

    /// <summary>403: sharing is turned off on this server.</summary>
    public const string SharingDisabledCode = "SharingDisabled";

    /// <summary>403: a query that uses an administrator-only field can't be shared.</summary>
    public const string SharedAdminOnlyFieldCode = "SharedAdminOnlyField";

    /// <summary>409: the owner already has a query of this name.</summary>
    public const string DuplicateNameCode = "DuplicateName";

    /// <summary>409: the owner has as many queries as the server allows.</summary>
    public const string QuotaReachedCode = "QuotaReached";

    /// <summary>409: the query changed since the caller read it.</summary>
    public const string ChangedCode = "IfUpdatedAt";

    /// <summary>409: a newer Smart Search wrote the query; it can't be run or changed here.</summary>
    public const string NeedsNewerVersionCode = "NeedsNewerVersion";

    /// <summary>403: auto-collections use the query, and only a collection manager may change it.</summary>
    public const string ManagerRequiredCode = "ManagerRequired";

    /// <summary>409: the change would break auto-collections that use the query.</summary>
    public const string BreaksAutoCollectionsCode = "BreaksAutoCollections";

    /// <summary>409: the change needs a confirmation (<c>AcknowledgeAutoCollectionWarnings</c>).</summary>
    public const string NeedsAttentionCode = "AutoCollectionsNeedAttention";

    /// <summary>409: auto-collections use the query (delete with <c>detachAutoCollections=true</c>).</summary>
    public const string ReferencedCode = "Referenced";

    /// <summary>400: no user of this server has the id.</summary>
    public const string UnknownUserCode = "UnknownUser";

    /// <summary>409: the auto-collections couldn't be loaded, so a change that could break one of them is refused.</summary>
    public const string StoreReadOnlyCode = "StoreReadOnly";

    /// <summary>Gets the 404 of a query the caller can't open.</summary>
    public static PluginException NotFound => new(PluginErrorKind.NotFound, "No saved query has this id.") { Code = NotFoundCode };

    /// <summary>Gets the 403 of a change to someone else's shared query.</summary>
    public static PluginException NotOwner => new(PluginErrorKind.Forbidden, "Only the query's owner or an administrator can change or delete it; you can duplicate it.") { Code = NotOwnerCode };

    /// <summary>Gets the 409 of a query a newer version wrote.</summary>
    public static PluginException NeedsNewerVersion => new(PluginErrorKind.Conflict, "This query was saved by a newer version of Smart Search: it can't be run or changed here. It can be duplicated, exported or deleted.") { Code = NeedsNewerVersionCode };

    /// <summary>
    /// Gets the 409 <c>StoreReadOnly</c> of a change refused because what references saved queries couldn't be loaded
    /// (<see cref="ISavedQueryReferenceGuard.ReferencesUnknown"/>).
    /// </summary>
    public static PluginException ReferencesUnknown => new(
        PluginErrorKind.Conflict,
        "The auto-collections couldn't be loaded, so it isn't known which of them use the saved queries. Until they load, a saved query can't be deleted, given to another user or have its query changed; its name, description and sharing can still be changed.")
    {
        Code = StoreReadOnlyCode
    };

    /// <summary>
    /// The 409 of a bulk change (an import, a snapshot restore, a remap) that would break references: the references in
    /// <c>autoCollections</c>, as an edit's refusal has them.
    /// </summary>
    /// <param name="references">What it would break.</param>
    /// <returns>The exception.</returns>
    /// <param name="message">The sentence; the bulk change's by default.</param>
    public static PluginException BreaksReferences(IReadOnlyList<SmartSearchAutoCollectionRefDto> references, string? message = null)
        => new(PluginErrorKind.Conflict, message ?? "This would remove or change saved queries that auto-collections use. Check the dry run; force=true does it anyway, and the auto-collections then show what is missing or changed.")
        {
            Code = BreaksAutoCollectionsCode,
            Extensions = new Dictionary<string, object?> { ["autoCollections"] = Problems(references) }
        };

    /// <summary>The 409 of a change that would give a user who isn't an administrator a query using an administrator-only field.</summary>
    /// <returns>The exception.</returns>
    public static PluginException AdminOnlyForUser()
        => new(PluginErrorKind.Conflict, "The query uses an administrator-only field, and only an administrator may own it.") { Code = QueryErrorCodes.AdminOnlyField };

    /// <summary>The 409 of a change that would take a user past <c>MaxSavedQueriesPerUser</c>.</summary>
    /// <param name="maxPerUser">The setting.</param>
    /// <returns>The exception.</returns>
    public static PluginException QuotaFor(int maxPerUser)
        => new(PluginErrorKind.Conflict, "The user already has as many saved queries as this server allows (" + maxPerUser.ToString(CultureInfo.InvariantCulture) + ").") { Code = QuotaReachedCode };

    /// <summary>A 400 <c>InvalidRequest</c> with one error.</summary>
    /// <param name="path">The parameter or property.</param>
    /// <param name="code">The error's code.</param>
    /// <param name="message">The message.</param>
    /// <returns>The exception.</returns>
    public static PluginException Invalid(string path, string code, string message)
        => new(PluginErrorKind.Invalid, message)
        {
            Code = SmartSearchRequestBinder.InvalidRequestCode,
            Extensions = new Dictionary<string, object?> { ["errors"] = new List<ProblemError> { new(path, code, message) } }
        };

    /// <summary>The refusal of a guarded change, or <c>null</c> when it was written.</summary>
    /// <param name="outcome">The guard's outcome.</param>
    /// <returns>The exception, or <c>null</c>.</returns>
    public static PluginException? Of(SavedQueryGuardOutcome outcome)
    {
        if (outcome.Status == SavedQueryGuardStatus.ReferencesUnknown)
        {
            return ReferencesUnknown;
        }

        var (kind, code, message) = outcome.Status switch
        {
            SavedQueryGuardStatus.Done => (PluginErrorKind.Invalid, string.Empty, string.Empty),
            SavedQueryGuardStatus.ManagerRequired => (PluginErrorKind.Forbidden, ManagerRequiredCode, "Auto-collections use this query: only a collection manager can change it. Save a copy instead."),
            SavedQueryGuardStatus.BreaksReferences => (PluginErrorKind.Conflict, BreaksAutoCollectionsCode, "This change would break auto-collections that use the query."),
            SavedQueryGuardStatus.NeedsAttention => (PluginErrorKind.Conflict, NeedsAttentionCode, "This change affects auto-collections that use the query; confirm it to save."),
            _ => (PluginErrorKind.Conflict, ReferencedCode, "Auto-collections use this query. Detach them (they keep a copy of it), then delete it.")
        };
        return outcome.Status == SavedQueryGuardStatus.Done
            ? null
            : new PluginException(kind, message)
            {
                Code = code,
                Extensions = new Dictionary<string, object?> { ["autoCollections"] = Problems(outcome.References) }
            };
    }

    private static List<AutoCollectionProblem> Problems(IReadOnlyList<SmartSearchAutoCollectionRefDto> references)
        => references.Select(r => new AutoCollectionProblem(Kit.Users.KnownUsers.FormatId(r.Id), r.Name, r.Messages)).ToList();

    /// <summary>One auto-collection in a problem's <c>autoCollections</c>.</summary>
    /// <param name="Id">Its id.</param>
    /// <param name="Name">Its name.</param>
    /// <param name="Messages">The reasons or warnings.</param>
    internal sealed record AutoCollectionProblem(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("messages")] IReadOnlyList<string> Messages);
}
