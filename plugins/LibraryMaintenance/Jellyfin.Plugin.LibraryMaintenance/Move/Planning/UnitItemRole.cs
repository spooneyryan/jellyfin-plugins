namespace Jellyfin.Plugin.LibraryMaintenance.Move.Planning;

/// <summary>
/// An item's part in a unit, as the move journal's additive <c>Role</c> names it (an additive field).
/// </summary>
internal enum UnitItemRole
{
    /// <summary>The item moved.</summary>
    Primary,

    /// <summary>A local alternate version (<c>LocalAlternateVersions</c>).</summary>
    Version,

    /// <summary>A stacked part (<c>AdditionalParts</c>).</summary>
    Part,

    /// <summary>An extra (<c>ExtraIds</c>).</summary>
    Extra
}
