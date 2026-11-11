namespace Jellyfin.Plugin.Kit.Web.Pages;

/// <summary>
/// The outcome of one registration attempt with Plugin Pages (<see cref="PluginPagesBridge.TryRegister"/>).
/// </summary>
/// <param name="Registered">True when Plugin Pages accepted the page.</param>
/// <param name="Api">Which Plugin Pages API took it (<see cref="PluginPagesBridge.Api3x"/>, <see cref="PluginPagesBridge.Api24"/>),
/// or why none did (<see cref="PluginPagesBridge.NotInstalled"/>, <see cref="PluginPagesBridge.InvalidPage"/>,
/// <see cref="PluginPagesBridge.UnexpectedInterface"/>, <see cref="PluginPagesBridge.Rejected"/>,
/// <see cref="PluginPagesBridge.Failed"/>).</param>
/// <param name="Detail">The Plugin Pages assembly and version, or the reason.</param>
internal sealed record PluginPagesRegistrationResult(bool Registered, string Api, string Detail);
