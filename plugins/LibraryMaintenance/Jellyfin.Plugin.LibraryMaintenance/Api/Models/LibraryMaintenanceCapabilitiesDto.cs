namespace Jellyfin.Plugin.LibraryMaintenance.Api.Models;

/// <summary>
/// What the signed-in user can do with Library Maintenance's web parts (<c>Capabilities</c>),
/// so the client shows the right controls. Cosmetic: every admin endpoint checks again. For a user who isn't an
/// administrator every flag is false.
/// </summary>
public sealed class LibraryMaintenanceCapabilitiesDto
{
    /// <summary>
    /// Gets or sets a value indicating whether the caller passes Jellyfin's <c>RequiresElevation</c> policy on this request.
    /// </summary>
    public bool IsAdmin { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether Move media is turned on (<c>MoveEnabled</c>). Off, the entries are
    /// hidden and the Move routes answer 403 <c>feature-disabled</c>.
    /// </summary>
    public bool MoveEnabled { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the server can move media at all: false on a host that isn't Windows,
    /// where the Move routes answer 503.
    /// </summary>
    public bool MoveSupported { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the caller can open the Move dialog and see a plan (an administrator, Move
    /// on, a Windows host).
    /// </summary>
    public bool CanPlanMove { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a planned move can run. False in the plan-only build and
    /// in the 1.1 development builds before the job side exists; the dialog hides its Move button then.
    /// </summary>
    public bool CanMove { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the item "⋮" menu shows the Move entry (<c>ShowInItemMenu</c>).
    /// </summary>
    public bool ShowMoveInItemMenu { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the details page shows the Move button (<c>ShowDetailPageButton</c>).
    /// </summary>
    public bool ShowMoveDetailPageButton { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the Move entry shows in the TV layout (<c>ShowInTvLayout</c>).
    /// </summary>
    public bool ShowMoveInTvLayout { get; set; }
}
