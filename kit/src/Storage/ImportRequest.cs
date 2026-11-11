using System;
using Jellyfin.Plugin.Kit.Api;

namespace Jellyfin.Plugin.Kit.Storage;

/// <summary>
/// The query of <c>POST /&lt;Route&gt;/Admin/Import?mode=Merge|Replace&amp;dryRun=true&amp;mapUsersBy=Id|Name</c>.
/// Controllers take the three values as strings (kit types are internal) and call <see cref="Parse"/>.
/// </summary>
/// <param name="Mode">Merge (the default) or Replace.</param>
/// <param name="DryRun">Whether nothing is written; true unless the request says <c>false</c>.</param>
/// <param name="MapUsersBy">Id (the default) or Name.</param>
internal sealed record ImportRequest(ImportMode Mode, bool DryRun, MapUsersBy MapUsersBy)
{
    /// <summary>
    /// Reads the query values, ignoring case. Missing values take the defaults: <c>Merge</c>, a dry run, <c>Id</c>.
    /// </summary>
    /// <param name="mode">The <c>mode</c> value.</param>
    /// <param name="dryRun">The <c>dryRun</c> value.</param>
    /// <param name="mapUsersBy">The <c>mapUsersBy</c> value.</param>
    /// <returns>The request.</returns>
    /// <exception cref="PluginException">400 for an unknown mode or mapping.</exception>
    public static ImportRequest Parse(string? mode, bool? dryRun, string? mapUsersBy)
        => new(
            ParseName(mode, ImportMode.Merge, "mode must be Merge or Replace."),
            dryRun ?? true,
            ParseName(mapUsersBy, MapUsersBy.Id, "mapUsersBy must be Id or Name."));

    // Names only (never numbers), ignoring case and surrounding spaces.
    private static TEnum ParseName<TEnum>(string? value, TEnum fallback, string message)
        where TEnum : struct, Enum
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return fallback;
        }

        foreach (var candidate in Enum.GetValues<TEnum>())
        {
            if (string.Equals(candidate.ToString(), text, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        throw PluginException.Invalid(message);
    }
}
