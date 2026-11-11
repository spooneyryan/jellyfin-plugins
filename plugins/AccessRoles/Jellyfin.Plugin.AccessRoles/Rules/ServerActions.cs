using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.ActionConstraints;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace Jellyfin.Plugin.AccessRoles.Rules;

/// <summary>
/// The running server's controller actions as <see cref="ApiActionShape"/>s, from MVC's action descriptors
/// (<c>IActionDescriptorCollectionProvider</c>): the same shape the manifest test reads from a DLL's metadata, so
/// Diagnostics can compare the manifest with what the server really routes (and so check the test's reading of MVC's rules).
/// MVC makes one descriptor per route; they are grouped by controller and action name.
/// </summary>
internal static class ServerActions
{
    /// <summary>
    /// Reads the actions of one assembly's controllers.
    /// </summary>
    /// <param name="provider">MVC's action descriptors.</param>
    /// <param name="assemblyName">The controllers' assembly, for example <c>Jellyfin.Api</c>.</param>
    /// <returns>The actions, by controller and action name.</returns>
    public static IReadOnlyList<ApiActionShape> Read(IActionDescriptorCollectionProvider provider, string assemblyName)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return FromDescriptors(Descriptors(provider).Where(d => string.Equals(d.ControllerTypeInfo.Assembly.GetName().Name, assemblyName, StringComparison.Ordinal)));
    }

    /// <summary>
    /// Reads a set of action descriptors as shapes.
    /// </summary>
    /// <param name="descriptors">MVC's descriptors (one per route).</param>
    /// <returns>The actions, by controller and action name.</returns>
    public static IReadOnlyList<ApiActionShape> FromDescriptors(IEnumerable<ControllerActionDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        return descriptors
            .GroupBy(d => (Controller: d.ControllerTypeInfo.FullName ?? d.ControllerTypeInfo.Name, d.ActionName, d.MethodInfo))
            .Select(g => new ApiActionShape(
                g.Key.Controller,
                g.Key.ActionName,
                g.Select(Route),
                g.SelectMany(d => d.EndpointMetadata.OfType<IAuthorizeData>()).Select(a => string.IsNullOrEmpty(a.Policy) ? ApiActionShape.DefaultPolicy : a.Policy),
                g.Any(d => d.EndpointMetadata.OfType<IAllowAnonymous>().Any()),
                g.SelectMany(d => d.Parameters).Select(p => ApiActionShape.FormatParameter(p.Name, p.BindingInfo?.BinderModelName))))
            .OrderBy(a => a.Controller, StringComparer.Ordinal)
            .ThenBy(a => a.Action, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Counts the controller actions of every assembly the server routes (Jellyfin's and each plugin's).
    /// </summary>
    /// <param name="provider">MVC's action descriptors.</param>
    /// <returns>The number of distinct actions per assembly name.</returns>
    public static IReadOnlyDictionary<string, int> CountByAssembly(IActionDescriptorCollectionProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return Descriptors(provider)
            .GroupBy(d => d.ControllerTypeInfo.Assembly.GetName().Name ?? "(unnamed)", StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(d => (d.ControllerTypeInfo.FullName, d.ActionName)).Distinct().Count(), StringComparer.Ordinal);
    }

    private static IEnumerable<ControllerActionDescriptor> Descriptors(IActionDescriptorCollectionProvider provider)
        => provider.ActionDescriptors.Items.OfType<ControllerActionDescriptor>();

    private static string Route(ControllerActionDescriptor descriptor)
        => ApiActionShape.FormatRoute(
            descriptor.ActionConstraints?.OfType<HttpMethodActionConstraint>().SelectMany(c => c.HttpMethods) ?? [],
            descriptor.AttributeRouteInfo?.Template);
}
