using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AccessRoles.Rules;

/// <summary>
/// One action of the <see cref="ActionManifest"/>: what Jellyfin.Api had for it when it was reviewed (its routes, policies
/// and parameters, which the manifest test compares with the DLL and Diagnostics with the running server), and what the
/// filter does with it: its rules, or none, with the reviewed reason in <see cref="Review"/>.
/// </summary>
internal sealed record ActionManifestEntry
{
    /// <summary>Gets the controller's full type name.</summary>
    public required string Controller { get; init; }

    /// <summary>Gets MVC's action name.</summary>
    public required string Action { get; init; }

    /// <summary>Gets the routes, each <c>"&lt;METHODS&gt; &lt;template&gt;"</c>.</summary>
    public IReadOnlyList<string> Routes { get; init; } = [];

    /// <summary>Gets the <c>[Authorize]</c> policies; empty when nothing asks for authorization.</summary>
    public IReadOnlyList<string> Policies { get; init; } = [];

    /// <summary>Gets a value indicating whether <c>[AllowAnonymous]</c> opens the action.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool AllowAnonymous { get; init; }

    /// <summary>
    /// Gets every parameter MVC binds for the action (<see cref="ApiActionShape.FormatParameter"/>: the argument's name, and
    /// the name it is bound from when an attribute names another), the arguments the rules read among them.
    /// </summary>
    /// <remarks>
    /// The scope parameters (the generic backstop's) are <see cref="ApiActionShape.ScopeParameters"/> of
    /// <see cref="ToShape"/>. No property of this record carries them, so a file of the first format (0.9.1), with its
    /// <c>ScopeParameters</c> field, fails the load instead of reading as actions without parameters.
    /// </remarks>
    public IReadOnlyList<string> Parameters { get; init; } = [];

    /// <summary>Gets what the filter does with the action; empty for "reviewed: no rule".</summary>
    public IReadOnlyList<ActionManifestRule> Rules { get; init; } = [];

    /// <summary>
    /// Gets the review: for an action without rules the one-line reason it needs none, for one with rules the
    /// reason for them.
    /// </summary>
    public string Review { get; init; } = string.Empty;

    /// <summary>Gets the key the manifest and the rule table use: <c>&lt;Controller&gt;.&lt;Action&gt;</c>.</summary>
    [JsonIgnore]
    public string Key => Controller + "." + Action;

    /// <summary>
    /// Gets the action's shape as reviewed.
    /// </summary>
    /// <returns>The shape.</returns>
    public ApiActionShape ToShape() => new(Controller, Action, Routes, Policies, AllowAnonymous, Parameters);
}
