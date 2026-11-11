using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.AccessRoles.Policy;

/// <summary>
/// The per-user lock around writes of a user's policy: <see cref="PolicyWriter"/> holds it from the
/// fresh read to <c>UpdatePolicyAsync</c>, and the global filter holds the same one around an admin's
/// <c>UpdateUserPolicy</c>, <c>UpdateUser</c> and <c>DeleteUser</c> of a managed user, so the two never interleave (a race
/// seen on 10.11.5). One lock per user, kept for the life of the server.
/// </summary>
internal sealed class UserPolicyLocks
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    /// <summary>
    /// Takes a user's lock, waiting at most <paramref name="timeout"/>.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="timeout">How long to wait.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The lease (dispose it to release the lock), or <c>null</c> when the lock wasn't free in time.</returns>
    public async Task<IDisposable?> TryAcquireAsync(Guid userId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var gate = _locks.GetOrAdd(userId, static _ => new SemaphoreSlim(1, 1));
        return await gate.WaitAsync(timeout, cancellationToken).ConfigureAwait(false) ? new Lease(gate) : null;
    }

    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                gate.Release();
            }
        }
    }
}
