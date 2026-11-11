using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AccessRoles.Access;
using Jellyfin.Plugin.AccessRoles.Rules;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AccessRoles.Sync;

/// <summary>
/// The library lifecycle's primary signal (the library structure hook, one of the library sync's
/// triggers): an action filter that only watches the seven <c>LibraryStructureController</c> actions the
/// manifest marks. Before the action it records the library the call affects (a removal's or a rename's id, from its
/// name, while the folder still exists); after a 2xx result it tells the sync, which reconciles at once. It never changes
/// a request or a result, never throws, and passes every other action straight on (one dictionary lookup).
/// </summary>
/// <remarks>
/// Since the settings locks it is no global filter of its own: Access Roles' one global filter, <c>AccessRulesActionFilter</c> at
/// order −10000 (verified on 10.11.5 to run before Jellyfin's own filters), hands it these seven actions;
/// the controller's actions are <c>FirstTimeSetupOrElevated</c>, so only administrators and API keys get this far once the
/// server is set up.
/// </remarks>
internal sealed class LibraryStructureFilter : IAsyncActionFilter
{
    /// <summary>The order it runs at: the global filter's −10000, inside <c>AccessRulesActionFilter</c> since 0.9.5.</summary>
    public const int Order = -10000;

    /// <summary>The controller whose actions it watches.</summary>
    public const string Controller = "Jellyfin.Api.Controllers.LibraryStructureController";

    private readonly IReadOnlyDictionary<string, LibraryStructureCallKind> _actions;
    private readonly LibrarySyncService _sync;
    private readonly ILibraryCatalogSource _catalog;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryStructureFilter"/> class.
    /// </summary>
    /// <param name="manifest">The action manifest: the actions with a <c>LibraryStructureHook</c> rule.</param>
    /// <param name="sync">The sync, told about each call.</param>
    /// <param name="catalog">The catalog, for a library's id from its name.</param>
    /// <param name="logger">The logger.</param>
    public LibraryStructureFilter(ActionManifest manifest, LibrarySyncService sync, ILibraryCatalogSource catalog, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(sync);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(logger);
        _actions = Actions(manifest);
        _sync = sync;
        _catalog = catalog;
        _logger = logger;
    }

    /// <summary>Gets the watched actions by name, with the kind of call each is.</summary>
    public IReadOnlyDictionary<string, LibraryStructureCallKind> WatchedActions => _actions;

    /// <summary>
    /// Gets whether the filter watches an action (one of <see cref="Controller"/>'s with the hook), found by name.
    /// </summary>
    /// <param name="descriptor">The action.</param>
    /// <returns><c>true</c> when it does.</returns>
    public bool Watches(ControllerActionDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return string.Equals(descriptor.ControllerTypeInfo.FullName, Controller, StringComparison.Ordinal) && _actions.ContainsKey(descriptor.ActionName);
    }

    /// <summary>
    /// Finds the actions a manifest marks with <c>LibraryStructureHook</c>, by name.
    /// </summary>
    /// <param name="manifest">The manifest.</param>
    /// <returns>The actions of <see cref="Controller"/>, with their kind.</returns>
    public static IReadOnlyDictionary<string, LibraryStructureCallKind> Actions(ActionManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return manifest.Actions
            .Where(a => string.Equals(a.Controller, Controller, StringComparison.Ordinal) && a.Rules.Any(r => r.Kind == RuleKind.LibraryStructureHook))
            .ToDictionary(
                a => a.Action,
                a => a.Action switch
                {
                    "AddVirtualFolder" => LibraryStructureCallKind.Add,
                    "RemoveVirtualFolder" => LibraryStructureCallKind.Remove,
                    "RenameVirtualFolder" => LibraryStructureCallKind.Rename,
                    _ => LibraryStructureCallKind.Update
                },
                StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        if (context.ActionDescriptor is not ControllerActionDescriptor descriptor
            || !string.Equals(descriptor.ControllerTypeInfo.FullName, Controller, StringComparison.Ordinal)
            || !_actions.TryGetValue(descriptor.ActionName, out var kind))
        {
            await next().ConfigureAwait(false);
            return;
        }

        LibraryStructureCall? call = null;
        try
        {
            call = Before(kind, context.ActionArguments);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Access Roles couldn't record the library structure call {Action} before it ran", descriptor.ActionName);
        }

        var executed = await next().ConfigureAwait(false);
        try
        {
            if (call is not null && Succeeded(executed))
            {
                await _sync.LibraryStructureChangedAsync(call, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            // The call succeeded; the backstop reconcile still sees what it changed.
            _logger.LogWarning(ex, "Access Roles couldn't record the library structure call {Action}", descriptor.ActionName);
        }
    }

    private static bool Succeeded(ActionExecutedContext executed)
    {
        if (executed.Exception is not null && !executed.ExceptionHandled)
        {
            return false;
        }

        return executed.Result switch
        {
            IStatusCodeActionResult { StatusCode: { } status } => status is >= 200 and < 300,
            _ => true
        };
    }

    private LibraryStructureCall Before(LibraryStructureCallKind kind, IDictionary<string, object?> arguments)
    {
        var name = arguments.TryGetValue("name", out var value) ? value as string : null;
        var newName = arguments.TryGetValue("newName", out var renamed) ? renamed as string : null;
        Guid? id = kind is LibraryStructureCallKind.Remove or LibraryStructureCallKind.Rename && !string.IsNullOrWhiteSpace(name)
            ? _catalog.IdOf(name)
            : null;
        return new LibraryStructureCall(kind, name, newName, id);
    }
}
