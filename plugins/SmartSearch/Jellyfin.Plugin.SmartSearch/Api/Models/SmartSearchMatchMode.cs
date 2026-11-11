namespace Jellyfin.Plugin.SmartSearch.Api.Models;

/// <summary>
/// How a group combines its children: every child matches, or at least one does.
/// </summary>
public enum SmartSearchMatchMode
{
    /// <summary>Every child matches (AND); a group without <c>Match</c> is this.</summary>
    All,

    /// <summary>At least one child matches (OR).</summary>
    Any
}
