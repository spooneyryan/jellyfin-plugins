namespace Jellyfin.Plugin.Kit.Api;

/// <summary>
/// The category of an expected failure (<see cref="PluginException"/>). It decides the HTTP status code and, for the two
/// 403 kinds the web client words differently, the ProblemDetails <c>type</c>.
/// </summary>
internal enum PluginErrorKind
{
    /// <summary>The request is invalid: 400.</summary>
    Invalid,

    /// <summary>The caller may not do this: 403.</summary>
    Forbidden,

    /// <summary>
    /// The feature is turned off by the admin: 403 with type <c>urn:jellyfin-plugin-kit:feature-disabled</c>, the only 403
    /// the client words as "turned off".
    /// </summary>
    FeatureDisabled,

    /// <summary>A setting is locked for the caller: 403 with type <c>urn:jellyfin-plugin-kit:locked</c>.</summary>
    Locked,

    /// <summary>The thing doesn't exist or isn't visible to the caller: 404.</summary>
    NotFound,

    /// <summary>The request conflicts with the current state: 409.</summary>
    Conflict,

    /// <summary>The request body is too large: 413.</summary>
    PayloadTooLarge,

    /// <summary>The service can't handle the request now, for example a read-only store or an inactive build: 503.</summary>
    Unavailable
}
