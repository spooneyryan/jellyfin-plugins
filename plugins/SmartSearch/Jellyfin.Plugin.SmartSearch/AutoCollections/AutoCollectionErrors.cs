using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.SmartSearch.Api;

namespace Jellyfin.Plugin.SmartSearch.AutoCollections;

/// <summary>The refusals of the auto-collection endpoints, each with a stable <c>code</c>.</summary>
internal static class AutoCollectionErrors
{
    /// <summary>403: the caller doesn't manage collections.</summary>
    public const string ManagerRequiredCode = "ManagerRequired";

    /// <summary>404: no auto-collection has the id.</summary>
    public const string NotFoundCode = "AutoCollectionNotFound";

    /// <summary>403: only the creator or an administrator changes an auto-collection's source.</summary>
    public const string NotCreatorCode = "NotCreator";

    /// <summary>The mixed-audience warning.</summary>
    public const string MixedAudienceMessage = "Some people who can see this collection can't open all of its items; they see only the ones they can.";

    /// <summary>Gets the 403 of a caller who doesn't manage collections.</summary>
    public static PluginException ManagerRequired => new(PluginErrorKind.Forbidden, "Only administrators and users allowed to manage collections can manage auto-collections.") { Code = ManagerRequiredCode };

    /// <summary>Gets the 404 of an id no auto-collection has.</summary>
    public static PluginException NotFound => new(PluginErrorKind.NotFound, "No auto-collection has this id.") { Code = NotFoundCode };

    /// <summary>Gets the 403 of a change of someone else's source.</summary>
    public static PluginException NotCreator => new(PluginErrorKind.Forbidden, "Only the auto-collection's creator or an administrator can change what it follows.") { Code = NotCreatorCode };

    /// <summary>Gets the 409 of a write to a store a newer version wrote (or that failed to upgrade).</summary>
    public static PluginException StoreReadOnly => new(PluginErrorKind.Conflict, "The auto-collections were saved by a newer version of Smart Search, or can't be read: they can't be changed here.") { Code = AutoCollectionReasons.StoreReadOnly };

    /// <summary>Gets the 403 of a delete whose collection can't be deleted by the caller (nothing is deleted).</summary>
    public static PluginException CannotDeleteCollection => new(PluginErrorKind.Forbidden, "The collection can't be deleted: it isn't this auto-collection's any more, or you can't see or delete it. Nothing was deleted.") { Code = "CannotDeleteCollection" };

    /// <summary>Gets the 403 of a reference to a saved query the creator doesn't own.</summary>
    public static PluginException NotQueryOwner => new(PluginErrorKind.Forbidden, "An auto-collection can follow only its creator's own saved queries. Ask for a copy (DuplicateSourceQuery) or use a copy of the query.") { Code = "NotQueryOwner" };

    /// <summary>Gets the 403 of an administrator pointing someone else's auto-collection at a saved query.</summary>
    public static PluginException SnapshotOnly => new(PluginErrorKind.Forbidden, "Someone else's auto-collection can only be switched to a copy of a query (Snapshot), not to a saved query.") { Code = "SnapshotOnly" };

    /// <summary>Gets the 403 of a change only the creator or an administrator may make.</summary>
    public static PluginException CreatorOnly => new(PluginErrorKind.Forbidden, "Only the auto-collection's creator or an administrator can change its name, source, evaluation user or whether it is on; other managers can change its look, safeguards and manual edits.") { Code = NotCreatorCode };

    /// <summary>Gets the 409 of a create whose members differ from the accepted preview by more than a little.</summary>
    public static PluginException PreviewStale => new(PluginErrorKind.Conflict, "The collection's members changed since the preview. Preview it again, then save.") { Code = "PreviewStale" };

    /// <summary>Gets the 409 of a detach of a definition that already has its own copy.</summary>
    public static PluginException AlreadySnapshot => new(PluginErrorKind.Conflict, "This auto-collection already follows its own copy of the query.") { Code = "AlreadySnapshot" };

    /// <summary>Gets the 409 of a detach whose saved query isn't the creator's any more: nothing of its owner's is copied.</summary>
    public static PluginException SourceOwnerChanged => new(PluginErrorKind.Conflict, "The saved query it follows now belongs to someone else, so it isn't copied. Choose a query of the creator's, or give it its own query.") { Code = AutoCollectionReasons.SourceOwnerChanged };

    /// <summary>Gets the 409 of a detach whose saved query is gone.</summary>
    public static PluginException QueryMissing => new(PluginErrorKind.Conflict, "The saved query is gone; there is nothing to copy.") { Code = AutoCollectionReasons.QueryMissing };

    /// <summary>Gets the 409 of a recreate of a definition whose collection is there.</summary>
    public static PluginException NotRecreatable => new(PluginErrorKind.Conflict, "Only an auto-collection whose collection was deleted, never made, or replaced can be created again.") { Code = "NotRecreatable" };

    /// <summary>Gets the 409 of a save whose saved query was deleted meanwhile.</summary>
    public static PluginException SourceQueryGone => new(PluginErrorKind.Conflict, "The saved query was deleted meanwhile. Choose another query.") { Code = AutoCollectionReasons.QueryMissing };

    /// <summary>Gets the 403 of an action only an administrator may take.</summary>
    public static PluginException AdministratorRequired => new(PluginErrorKind.Forbidden, "Only an administrator can give an auto-collection to another user.") { Code = "AdministratorRequired" };

    /// <summary>Gets the 409 of a reassignment while the creator still exists.</summary>
    public static PluginException CreatorNotMissing => new(PluginErrorKind.Conflict, "Only an auto-collection whose creator was deleted can be given to another user.") { Code = "CreatorNotMissing" };

    /// <summary>Gets the 409 of a guard action when nothing is held, or something else is held now.</summary>
    public static PluginException GuardNotPending => new(PluginErrorKind.Conflict, "No removals are held for this auto-collection, or they changed since they were read. Read it again.") { Code = "GuardNotPending" };

    /// <summary>Gets the 409 of a guard action naming another version of the hold: its removals changed since they were read.</summary>
    public static PluginException HoldChanged => new(PluginErrorKind.Conflict, "The held removals changed since they were read. Read them again and review the new list.") { Code = "PreviewStale" };

    /// <summary>Gets the 409 of a refresh of a definition that is off.</summary>
    public static PluginException Disabled => new(PluginErrorKind.Conflict, "This auto-collection is off; turn it on to update it.") { Code = "AutoCollectionDisabled" };

    /// <summary>Gets the refusal while auto-collections are off in the settings.</summary>
    public static PluginException FeatureOff => new(PluginErrorKind.FeatureDisabled, "Auto-collections are turned off in Smart Search's settings.") { Code = "AutoCollectionsDisabled" };

    /// <summary>Gets the 409 of a create for a definition whose collection exists.</summary>
    public static PluginException HasCollection => new(PluginErrorKind.Conflict, "This auto-collection has a collection; only one whose collection was deleted can be created again.") { Code = "HasCollection" };

    /// <summary>The 409 of a Keep of more held items than can be pinned (the hold stays).</summary>
    /// <param name="held">The held members.</param>
    /// <param name="max">The most pins.</param>
    /// <returns>The exception.</returns>
    public static PluginException TooManyToKeep(int held, int max)
        => new(PluginErrorKind.Conflict, held + " items are held; with the items already pinned that is more than the " + max + " that can be pinned. Nothing was changed: apply the removals, or change the rules so fewer go.") { Code = "TooManyToKeep" };

    /// <summary>The 409 of a name a collection uses, with what is in the way and free names close to it.</summary>
    /// <param name="check">The name check.</param>
    /// <returns>The exception.</returns>
    public static PluginException NameTaken(CollectionNameCheck check)
        => new(PluginErrorKind.Conflict, "A collection named \"" + check.Name + "\" (or using its folder) exists. Choose another name.")
        {
            Code = AutoCollectionReasons.NameTaken,
            Extensions = new Dictionary<string, object?>
            {
                ["conflicts"] = check.Conflicts.Select(c => new NameConflictProblem(c.Kind.ToString(), c.Id?.ToString("N"), c.Name)).ToList(),
                ["suggestions"] = check.Suggestions
            }
        };

    /// <summary>A 400 <c>InvalidRequest</c> with one error.</summary>
    /// <param name="path">The property.</param>
    /// <param name="code">The error's code.</param>
    /// <param name="message">The message.</param>
    /// <returns>The exception.</returns>
    public static PluginException Invalid(string path, string code, string message)
        => new(PluginErrorKind.Invalid, message)
        {
            Code = SmartSearchRequestBinder.InvalidRequestCode,
            Extensions = new Dictionary<string, object?> { ["errors"] = new List<ProblemError> { new(path, code, message) } }
        };

    /// <summary>The 400 of a membership over the cap.</summary>
    /// <param name="count">The members.</param>
    /// <param name="cap">The cap.</param>
    /// <returns>The exception.</returns>
    public static PluginException TooManyMembers(int count, int cap)
        => new(PluginErrorKind.Invalid, "The collection would have " + count + " items; at most " + cap + " are allowed. Narrow the query or give it a limit.") { Code = AutoCollectionReasons.TooManyMembers };

    /// <summary>
    /// The refusal of a definition that failed its check: 409 for one a newer version wrote, 403 for an evaluation user the
    /// caller may not choose, 400 otherwise; the code is the check's reason, the messages ride as <c>errors</c>.
    /// </summary>
    /// <param name="check">The failed check.</param>
    /// <returns>The exception.</returns>
    public static PluginException Of(AutoCollectionCheck check)
    {
        var kind = check.Status == SmartSearchAutoCollectionStatus.NeedsNewerVersion ? PluginErrorKind.Conflict
            : check.Reason == AutoCollectionReasons.EvaluationUserNotAllowed ? PluginErrorKind.Forbidden
            : PluginErrorKind.Invalid;
        IReadOnlyList<string> messages = check.Messages.Count == 0 ? ["The auto-collection can't be used: " + check.Reason + "."] : check.Messages;
        return new PluginException(kind, string.Join(" ", messages))
        {
            Code = check.Reason ?? check.Status.ToString(),
            Extensions = new Dictionary<string, object?> { ["errors"] = messages.Select(m => new ProblemError(string.Empty, check.Reason ?? string.Empty, m)).ToList() }
        };
    }

    /// <summary>One conflict in a problem's <c>conflicts</c>.</summary>
    /// <param name="Kind">What is in the way.</param>
    /// <param name="Id">Its id, when there is one.</param>
    /// <param name="Name">Its name.</param>
    internal sealed record NameConflictProblem(
        [property: System.Text.Json.Serialization.JsonPropertyName("kind")] string Kind,
        [property: System.Text.Json.Serialization.JsonPropertyName("id")] string? Id,
        [property: System.Text.Json.Serialization.JsonPropertyName("name")] string? Name);
}
