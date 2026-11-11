using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Kit.Api;

namespace Jellyfin.Plugin.SmartSearch.Query.Execution;

/// <summary>
/// The engine's refusals: a search it won't run to the end, never a partial result that looks complete.
/// <see cref="PluginException.Code"/> (the kit's, set by every factory here) is the response's <c>code</c>;
/// <see cref="PluginException.Kind"/> its status (<see cref="QueryTooBroadCode"/> also carries <c>candidateCount</c> and
/// <c>kinds</c>): 400 for <see cref="QueryTooBroadCode"/> and
/// <see cref="FieldUnavailableCode"/>, 503 for <see cref="TimeoutCode"/> and <see cref="BusyCode"/>.
/// </summary>
[SuppressMessage(
    "Design",
    "CA1032:Implement standard exception constructors",
    Justification = "Created only through the factories below, each with its code; the kit's filter maps it before it leaves the plugin.")]
[SuppressMessage(
    "Design",
    "CA1064:Exceptions should be public",
    Justification = "Internal like PluginException (kit and plugin types are internal); the controller-level filter maps it.")]
internal sealed class SmartSearchRefusedException : PluginException
{
    /// <summary>The code of a search that would check more than <c>MaxCandidates</c> items (400).</summary>
    public const string QueryTooBroadCode = "QueryTooBroad";

    /// <summary>The code of a search that needs a field no provider can answer (400).</summary>
    public const string FieldUnavailableCode = "FieldUnavailable";

    /// <summary>The code of a search that ran past <c>SearchTimeoutSeconds</c> (503).</summary>
    public const string TimeoutCode = "Timeout";

    /// <summary>The code of a search that waited too long for one of the <c>MaxConcurrentSearches</c> slots (503).</summary>
    public const string BusyCode = "Busy";

    /// <summary>The <see cref="BusyCode"/> message of a lookup whose value index couldn't be built in time.</summary>
    public const string IndexBusyMessage = "The server is busy reading the libraries. Try again in a moment.";

    private SmartSearchRefusedException(PluginErrorKind kind, string code, string message)
        : base(kind, message)
    {
        Code = code;
    }

    /// <summary>Gets, for <see cref="QueryTooBroadCode"/>, how many items the search would check (at least).</summary>
    public int? CandidateCount { get; private init; }

    /// <summary>Gets, for <see cref="QueryTooBroadCode"/>, the kinds the query searches (the client picks its hint by them).</summary>
    public IReadOnlyList<BaseItemKind> Kinds { get; private init; } = [];

    /// <summary>Creates the <see cref="QueryTooBroadCode"/> refusal.</summary>
    /// <param name="candidateCount">How many items the search would check (at least).</param>
    /// <param name="maxCandidates">The cap.</param>
    /// <param name="kinds">The query's kinds.</param>
    /// <returns>The refusal.</returns>
    public static SmartSearchRefusedException QueryTooBroad(int candidateCount, int maxCandidates, IReadOnlyCollection<BaseItemKind> kinds)
    {
        ArgumentNullException.ThrowIfNull(kinds);
        var hint = kinds.Count == 1 && kinds.Contains(BaseItemKind.Episode)
            ? "Add a series rule (Series is …)."
            : "Add a library, type, year or genre rule.";
        var message = string.Format(CultureInfo.InvariantCulture, "This search would check {0:N0} items (at most {1:N0} are allowed). {2}", candidateCount, maxCandidates, hint);
        var kindList = kinds.ToList();
        return new SmartSearchRefusedException(PluginErrorKind.Invalid, QueryTooBroadCode, message)
        {
            CandidateCount = candidateCount,
            Kinds = kindList,

            // The response's candidateCount and kinds: the client words its hint by them.
            Extensions = new Dictionary<string, object?> { ["candidateCount"] = candidateCount, ["kinds"] = kindList.Select(k => k.ToString()).ToList() }
        };
    }

    /// <summary>Creates the <see cref="FieldUnavailableCode"/> refusal.</summary>
    /// <param name="fieldIds">The fields nothing can answer.</param>
    /// <returns>The refusal.</returns>
    public static SmartSearchRefusedException FieldUnavailable(IEnumerable<string> fieldIds)
        => new(PluginErrorKind.Invalid, FieldUnavailableCode, "This search uses a field this server can't answer: " + string.Join(", ", fieldIds) + ".");

    /// <summary>Creates the <see cref="TimeoutCode"/> refusal.</summary>
    /// <param name="seconds">The timeout.</param>
    /// <returns>The refusal.</returns>
    public static SmartSearchRefusedException Timeout(int seconds)
        => new(PluginErrorKind.Unavailable, TimeoutCode, string.Format(CultureInfo.InvariantCulture, "The search took longer than {0} seconds and was stopped. Narrow it with more rules and try again.", seconds));

    /// <summary>Creates the <see cref="BusyCode"/> refusal.</summary>
    /// <returns>The refusal.</returns>
    public static SmartSearchRefusedException Busy()
        => Busy("The server is running other searches. Try again in a moment.");

    /// <summary>Creates the <see cref="BusyCode"/> refusal with a message of its own.</summary>
    /// <param name="message">The message.</param>
    /// <returns>The refusal.</returns>
    public static SmartSearchRefusedException Busy(string message)
        => new(PluginErrorKind.Unavailable, BusyCode, message);
}
