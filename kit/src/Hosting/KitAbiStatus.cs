using System.Collections.Generic;

namespace Jellyfin.Plugin.Kit.Hosting;

/// <summary>
/// The outcome of <see cref="KitAbiGuard"/>: whether this build runs on the server's Jellyfin version. Registered as a
/// singleton in every case, and the only service an inert build registers. Shown in the
/// <see cref="Api.KitAbiGateAttribute"/> 503 body and, later, on the Diagnostics tab (<c>Abi:{Target, Server, Active}</c>).
/// </summary>
internal sealed class KitAbiStatus
{
    /// <summary>
    /// Initializes a new instance of the <see cref="KitAbiStatus"/> class.
    /// </summary>
    /// <param name="pluginName">The plugin's display name.</param>
    /// <param name="target">The Jellyfin line the build is for, or <c>unknown</c>.</param>
    /// <param name="server">The server's version, or <c>null</c> when it couldn't be read.</param>
    /// <param name="isActive">Whether the plugin runs.</param>
    /// <param name="message">A sentence for the log and the 503 body.</param>
    public KitAbiStatus(string pluginName, string target, string? server, bool isActive, string message)
    {
        PluginName = pluginName;
        Target = target;
        Server = server;
        IsActive = isActive;
        Message = message;
    }

    /// <summary>Gets the plugin's display name.</summary>
    public string PluginName { get; }

    /// <summary>Gets the Jellyfin line the build is for (<c>10.11</c>, <c>12</c>), or <c>unknown</c>.</summary>
    public string Target { get; }

    /// <summary>Gets the server's version, or <c>null</c> when it couldn't be read.</summary>
    public string? Server { get; }

    /// <summary>Gets a value indicating whether the plugin runs; when <c>false</c> it registered nothing else.</summary>
    public bool IsActive { get; }

    /// <summary>Gets the sentence for the log and the 503 body.</summary>
    public string Message { get; }

    /// <summary>
    /// Gets a value indicating whether another copy of the plugin was loaded beside this one and this copy stands down
    /// (<see cref="KitDuplicateGuard"/>). Its controllers then drop out of routing, so the other copy's identical routes
    /// answer (<see cref="Api.KitAbiGateAttribute"/>).
    /// </summary>
    public bool IsDuplicate { get; init; }

    /// <summary>
    /// Gets this status for a copy that stands down beside another copy: inactive, with <paramref name="message"/>.
    /// </summary>
    /// <param name="message">The duplicate guard's sentence; empty keeps this status's own (an inert build's).</param>
    /// <returns>The status.</returns>
    public KitAbiStatus AsDuplicate(string message)
        => new(PluginName, Target, Server, false, string.IsNullOrEmpty(message) ? Message : message) { IsDuplicate = true };

    /// <summary>
    /// Gets the status as the <c>{Target, Server, Active}</c> object of the Diagnostics shape.
    /// </summary>
    /// <returns>The summary.</returns>
    public IReadOnlyDictionary<string, object?> ToSummary() => new Dictionary<string, object?>
    {
        ["Target"] = Target,
        ["Server"] = Server,
        ["Active"] = IsActive
    };
}
