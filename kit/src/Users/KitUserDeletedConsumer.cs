using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Events.Users;
using MediaBrowser.Controller.Events;

namespace Jellyfin.Plugin.Kit.Users;

/// <summary>
/// Applies the plugin's <c>DeletedUserPolicy</c> when Jellyfin deletes a user. Registered scoped by
/// <c>AddKitUsers</c>; Jellyfin's <c>EventManager</c> resolves every <see cref="IEventConsumer{T}"/> from a new scope and
/// awaits it after the user is gone (<c>v10.11.5:Jellyfin.Server.Implementations/Users/UserManager.cs:282</c>,
/// <c>Events/EventManager.cs:43-63</c>), and logs what escapes. The work is <see cref="UserReconciler.HandleUserDeletedAsync"/>.
/// </summary>
internal sealed class KitUserDeletedConsumer : IEventConsumer<UserDeletedEventArgs>
{
    private readonly UserReconciler _reconciler;

    /// <summary>
    /// Initializes a new instance of the <see cref="KitUserDeletedConsumer"/> class.
    /// </summary>
    /// <param name="reconciler">The plugin's reconciler, which knows its owners.</param>
    public KitUserDeletedConsumer(UserReconciler reconciler)
    {
        ArgumentNullException.ThrowIfNull(reconciler);
        _reconciler = reconciler;
    }

    /// <inheritdoc />
    public Task OnEvent(UserDeletedEventArgs eventArgs)
    {
        ArgumentNullException.ThrowIfNull(eventArgs);
        return eventArgs.Argument is { } user ? _reconciler.HandleUserDeletedAsync(user.Id, CancellationToken.None) : Task.CompletedTask;
    }
}
