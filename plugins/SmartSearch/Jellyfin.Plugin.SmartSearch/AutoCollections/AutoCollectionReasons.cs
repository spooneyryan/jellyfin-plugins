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

/// <summary>The machine-readable reasons of stopped runs and refused saves.</summary>
internal static class AutoCollectionReasons
{
    /// <summary>The definitions' store is read-only.</summary>
    public const string StoreReadOnly = "StoreReadOnly";

    /// <summary>The definition has fields or values of a newer version.</summary>
    public const string NewerDefinition = "NewerDefinition";

    /// <summary>The creator was deleted.</summary>
    public const string CreatorDeleted = "CreatorDeleted";

    /// <summary>No source.</summary>
    public const string SourceMissing = "SourceMissing";

    /// <summary>The referenced query is gone.</summary>
    public const string QueryMissing = "QueryMissing";

    /// <summary>The referenced query belongs to someone else now.</summary>
    public const string SourceOwnerChanged = "SourceOwnerChanged";

    /// <summary>The query is of a newer version.</summary>
    public const string QueryUnsupported = "QueryUnsupported";

    /// <summary>The query has errors.</summary>
    public const string InvalidQuery = "InvalidQuery";

    /// <summary>The query uses an administrator-only field and the creator isn't one.</summary>
    public const string AdminOnlyField = "AdminOnlyField";

    /// <summary>A field the server can't answer now.</summary>
    public const string FieldUnavailable = "FieldUnavailable";

    /// <summary>The query's kinds include collections.</summary>
    public const string BoxSetKind = "BoxSetKind";

    /// <summary>A kind a collection doesn't hold.</summary>
    public const string KindNotAllowed = "KindNotAllowed";

    /// <summary>An <c>InCollection</c> rule names an auto-collection.</summary>
    public const string CycleWithAutoCollection = "CycleWithAutoCollection";

    /// <summary>The query's limit is above <c>MaxMembersPerCollection</c>.</summary>
    public const string LimitAboveCap = "LimitAboveCap";

    /// <summary>Per-user rules without an evaluation user.</summary>
    public const string EvaluationUserRequired = "EvaluationUserRequired";

    /// <summary>The evaluation user was deleted.</summary>
    public const string EvaluationUserDeleted = "EvaluationUserDeleted";

    /// <summary>A non-administrator named another user.</summary>
    public const string EvaluationUserNotAllowed = "EvaluationUserNotAllowed";

    /// <summary>An evaluation user for a query without per-user rules.</summary>
    public const string EvaluationUserNotNeeded = "EvaluationUserNotNeeded";

    /// <summary>More members than <c>MaxMembersPerCollection</c>.</summary>
    public const string TooManyMembers = "TooManyMembers";

    /// <summary>The collection was deleted (no collection carries the mark).</summary>
    public const string CollectionDeleted = "CollectionDeleted";

    /// <summary>The collection at the stored id no longer carries the mark (someone else's now).</summary>
    public const string TetherMissing = "TetherMissing";

    /// <summary>A collection already uses the name, or its folder.</summary>
    public const string NameTaken = "NameTaken";

    /// <summary>Queued in a pass that waits for a library scan to end.</summary>
    public const string WaitingForScan = "WaitingForScan";

    /// <summary>
    /// Items added (or removed) outside the plugin would take the pins (or excludes) over their limit: nothing is
    /// reverted or written until a manager makes room or switches the definition to Overwrite.
    /// </summary>
    public const string TooManyManualEdits = "TooManyManualEdits";

    /// <summary>The run failed with an unexpected error; the next run tries again.</summary>
    public const string RunFailed = "RunFailed";

    /// <summary>
    /// What stopped a run, in words any collection manager may read: no paths, names or query details (the full message
    /// is kept for administrators).
    /// </summary>
    /// <param name="reason">The run's reason code.</param>
    /// <returns>The text.</returns>
    public static string SafeText(string? reason) => reason switch
    {
        QueryMissing or SourceMissing => "The saved query it follows is gone.",
        SourceOwnerChanged => "The saved query it follows now belongs to someone else.",
        QueryUnsupported or InvalidQuery => "Its query can't be used by this version of Smart Search.",
        AdminOnlyField => "Its query uses a field only an administrator's auto-collection may use.",
        FieldUnavailable => "Its query uses a field that isn't available on this server.",
        BoxSetKind or KindNotAllowed => "Its query searches a kind of item a collection can't hold.",
        CycleWithAutoCollection => "Its query depends on its own collection.",
        LimitAboveCap or TooManyMembers => "The collection would have more items than allowed.",
        CreatorDeleted => "The user who made it no longer exists.",
        EvaluationUserRequired or EvaluationUserDeleted or EvaluationUserNotAllowed or EvaluationUserNotNeeded => "Its evaluation user needs a look.",
        NewerDefinition or StoreReadOnly => "It was saved by a newer version of Smart Search.",
        NameTaken => "Another collection uses its name.",
        TooManyManualEdits => "More items were added or removed outside the rules than can be kept as pins or excludes.",
        Query.Execution.SmartSearchRefusedException.BusyCode => "The server was busy; the next update tries again.",
        Query.Execution.SmartSearchRefusedException.TimeoutCode => "Its query took too long; the next update tries again.",
        Query.Execution.SmartSearchRefusedException.QueryTooBroadCode => "Its query checks too many items; narrow it.",
        _ => "The last update failed; the next one tries again. An administrator can see why."
    };
}
