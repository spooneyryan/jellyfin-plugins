namespace Jellyfin.Plugin.SmartSearch.Api.Models;

/// <summary>
/// The unit of a relative date rule (<c>InLast</c>, <c>NotInLast</c>).
/// </summary>
public enum SmartSearchRelativeUnit
{
    /// <summary>Days.</summary>
    Days,

    /// <summary>Weeks of seven days.</summary>
    Weeks,

    /// <summary>Calendar months.</summary>
    Months,

    /// <summary>Calendar years.</summary>
    Years
}
