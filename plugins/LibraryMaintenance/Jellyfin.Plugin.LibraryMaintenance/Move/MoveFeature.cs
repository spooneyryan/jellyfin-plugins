using System;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.LibraryMaintenance.Configuration;

namespace Jellyfin.Plugin.LibraryMaintenance.Move;

/// <summary>
/// Whether Move media answers: <c>MoveEnabled</c> off is the kit's <c>FeatureDisabled</c> (403,
/// <c>urn:jellyfin-plugin-kit:feature-disabled</c>), a host that isn't Windows is <c>Unavailable</c> (503,
/// <c>move.unsupported-os</c>; the move primitives are Win32), and whether this build can run a move at all
/// (<see cref="CanMove"/>).
/// </summary>
internal sealed class MoveFeature
{
    /// <summary>The 403's message (the kit client words it as "turned off").</summary>
    public const string DisabledMessage = "Moving media is turned off in Library Maintenance's settings.";

    /// <summary>The 503's message on a host that isn't Windows.</summary>
    public const string UnsupportedOsMessage = "Moving media is only supported on Windows in this version.";

    /// <summary>
    /// Whether this build runs move jobs: false until the job side exists, so 1.1.0.N development builds only
    /// plan. <c>POST /Jobs</c> comes with it.
    /// </summary>
    public const bool HasJobs = false;

    private readonly IPluginConfigurationProvider _configuration;

    /// <summary>
    /// Initializes a new instance of the <see cref="MoveFeature"/> class.
    /// </summary>
    /// <param name="configuration">The plugin's settings.</param>
    /// <param name="isSupportedOs">Whether the host can move; by default <see cref="OperatingSystem.IsWindows"/>. Tests
    /// simulate another host with false.</param>
    public MoveFeature(IPluginConfigurationProvider configuration, Func<bool>? isSupportedOs = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _configuration = configuration;
        IsSupportedOs = (isSupportedOs ?? OperatingSystem.IsWindows)();
    }

    /// <summary>
    /// Gets a value indicating whether this is the plan-only build: compiled with
    /// <c>MOVE_PREVIEW_ONLY</c> (<c>-p:MovePreviewOnly=true</c>), it never runs a move whatever its version.
    /// </summary>
    public static bool IsPreviewOnlyBuild =>
#if MOVE_PREVIEW_ONLY
        true;
#else
        false;
#endif

    /// <summary>Gets a value indicating whether the host can move media (Windows).</summary>
    public bool IsSupportedOs { get; }

    /// <summary>Gets the current settings.</summary>
    public MoveSettings Settings => MoveSettings.From(_configuration.GetConfiguration());

    /// <summary>Gets a value indicating whether moves can be planned: the feature is on and the host can move.</summary>
    public bool CanPlan => IsSupportedOs && Settings.Enabled;

    /// <summary>
    /// Gets a value indicating whether a planned move can run (<c>Capabilities.CanMove</c>): moves can be
    /// planned, the build has the job side and isn't the plan-only build. The dialog hides its Move button otherwise.
    /// </summary>
    public bool CanMove => CanPlan && IsMoveBuild(HasJobs, IsPreviewOnlyBuild);

    /// <summary>
    /// Gets whether a build with these traits runs moves (the rule behind <see cref="CanMove"/>, for its tests).
    /// </summary>
    /// <param name="hasJobs">Whether the job side is built.</param>
    /// <param name="previewOnly">Whether it is the plan-only build.</param>
    /// <returns>True when it runs moves.</returns>
    public static bool IsMoveBuild(bool hasJobs, bool previewOnly) => hasJobs && !previewOnly;

    /// <summary>
    /// Throws unless Move answers: 403 <c>FeatureDisabled</c> when it is off, else 503 <c>move.unsupported-os</c> on a host
    /// that isn't Windows. The disabled check comes first, so a turned-off feature reads the same on every host.
    /// </summary>
    /// <exception cref="PluginException">When Move doesn't answer.</exception>
    public void RequireAvailable()
    {
        if (!Settings.Enabled)
        {
            throw PluginException.FeatureDisabled(DisabledMessage);
        }

        if (!IsSupportedOs)
        {
            throw new PluginException(PluginErrorKind.Unavailable, UnsupportedOsMessage) { Code = MoveErrorCodes.UnsupportedOs };
        }
    }
}
