using System;
using Jellyfin.Plugin.AccessRoles.Configuration;
using Jellyfin.Plugin.AccessRoles.Storage;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.AccessRoles.Services;

/// <summary>
/// What the Data tab's actions need besides the roles API's: the data folder (the snapshots and the library
/// sync's hold), the settings an export carries, this server's id (an export of this server may fill in a missing ledger
/// entry) and the server's scheduled tasks (<i>Run maintenance now</i>).
/// </summary>
/// <param name="Paths">The data folder's paths.</param>
/// <param name="Configuration">The plugin configuration now.</param>
/// <param name="ServerId">This server's id (<c>SystemId</c>), or <c>null</c>.</param>
/// <param name="Tasks">The server's scheduled tasks, or <c>null</c> while they aren't available.</param>
internal sealed record AccessRolesDataSupport(AccessRolesPaths Paths, Func<PluginConfiguration> Configuration, string? ServerId, Func<ITaskManager?> Tasks);
