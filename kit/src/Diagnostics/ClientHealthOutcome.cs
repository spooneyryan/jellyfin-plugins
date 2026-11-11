namespace Jellyfin.Plugin.Kit.Diagnostics;

/// <summary>
/// What happened to a client health report. The endpoint answers 204 for every outcome but
/// <see cref="TooLarge"/> (413).
/// </summary>
internal enum ClientHealthOutcome
{
    /// <summary>Recorded (and logged, the first time for its anchor and web version).</summary>
    Accepted,

    /// <summary>Dropped: a field broke its rule, or an anchor or view isn't declared.</summary>
    Invalid,

    /// <summary>Dropped: the user's last accepted report is less than ten minutes old.</summary>
    RateLimited,

    /// <summary>
    /// Dropped: the user's accepted reports already named <see cref="ClientHealthRegistry.MaxWebClientsPerUser"/> other web
    /// clients (<see cref="ClientHealthReport.WebKey"/>: a web version, or a native app without one) since the server
    /// started. A real user has one to three; a forger who invents versions to fill Diagnostics has many.
    /// </summary>
    TooManyWebClients,

    /// <summary>Dropped: the plugin's <c>ReportWebAnchorProblems</c> setting is off.</summary>
    Disabled,

    /// <summary>Refused with 413: the body is over 16 KB.</summary>
    TooLarge,

    /// <summary>
    /// Counted per Diagnostics entry, not per report: an accepted report's new anchor, view and web version found the
    /// Diagnostics list full, and the entry updated longest ago made room for it.
    /// </summary>
    Overflow
}
