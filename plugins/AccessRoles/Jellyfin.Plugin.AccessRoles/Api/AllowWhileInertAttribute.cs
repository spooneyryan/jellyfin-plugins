using System;

namespace Jellyfin.Plugin.AccessRoles.Api;

/// <summary>
/// An action <see cref="AccessRolesInertGateAttribute"/> lets through while the plugin is inert: only the admin page's
/// script (an inert start registers nothing but the admin page).
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class AllowWhileInertAttribute : Attribute
{
}
