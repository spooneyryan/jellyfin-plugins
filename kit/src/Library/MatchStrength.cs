namespace Jellyfin.Plugin.Kit.Library;

/// <summary>
/// How closely a library item matches an <see cref="IdentitySnapshot"/>, weakest first.
/// </summary>
internal enum MatchStrength
{
    /// <summary>Nothing matched.</summary>
    None,

    /// <summary>
    /// The same kind, name and year (for episodes and seasons: the series name and the numbers). Only ever suggested,
    /// never applied automatically.
    /// </summary>
    Weak,

    /// <summary>
    /// The same kind and a shared provider id with no conflicting one (for episodes and seasons also the series' provider
    /// ids plus the season and episode numbers). Applied only when it is the single one and the consumer's
    /// <see cref="RelinkPolicy"/> allows and validates it.
    /// </summary>
    Strong,

    /// <summary>The same id, a Committed move-journal line, or the same path. Always applied.</summary>
    Exact
}
