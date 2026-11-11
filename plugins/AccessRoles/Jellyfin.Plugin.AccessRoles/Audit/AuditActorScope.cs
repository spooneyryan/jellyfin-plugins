using System;
using System.Threading;

namespace Jellyfin.Plugin.AccessRoles.Audit;

/// <summary>
/// The actor of audit lines written below a call that doesn't carry one: the admin service sets it around the kit's
/// <i>Apply deleted-user policy</i> and <i>Remap by name</i>, which reach the store's user-data owner through the kit's
/// reconciler, so their <c>UserDeleted</c> and <c>UsersRemapped</c> lines name the administrator (or the API key) who asked;
/// without it (Jellyfin deleting a user, the daily maintenance) the lines are the plugin's own, <c>System</c>.
/// </summary>
/// <remarks>The value flows with the asynchronous call (<see cref="AsyncLocal{T}"/>), so concurrent requests don't see each other's.</remarks>
internal static class AuditActorScope
{
    private static readonly AsyncLocal<AuditActor?> _current = new();

    /// <summary>Gets the actor of the call in progress, or <c>null</c>.</summary>
    public static AuditActor? Current => _current.Value;

    /// <summary>
    /// Sets the actor until the returned scope is disposed.
    /// </summary>
    /// <param name="actor">The actor.</param>
    /// <returns>The scope; disposing it puts the previous actor back.</returns>
    public static IDisposable Begin(AuditActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var previous = _current.Value;
        _current.Value = actor;
        return new Scope(previous);
    }

    private sealed class Scope(AuditActor? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _current.Value = previous;
            }
        }
    }
}
