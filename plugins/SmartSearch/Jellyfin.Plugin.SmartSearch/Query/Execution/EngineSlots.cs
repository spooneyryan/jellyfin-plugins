using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.SmartSearch.Query.Execution;

/// <summary>
/// The engine's admission control: searches and previews share
/// <c>MaxConcurrentSearches</c> slots, waiting up to <see cref="Wait"/> for one before they are refused (503 <c>Busy</c>);
/// the auto-collection worker has one slot of its own, so a pass never gets a 503 and never takes a search's slot.
/// Peak memory is therefore bounded by <c>MaxConcurrentSearches + 1</c> evaluations.
/// </summary>
/// <remarks>
/// A user runs one evaluation at a time: their next one first waits for their own slot, within the same
/// <see cref="Wait"/>, so one user can't hold every search slot and turn everyone else away.
/// </remarks>
internal sealed class EngineSlots : IDisposable
{
    /// <summary>How long a search waits for a slot by default.</summary>
    public static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(10);

    private readonly SemaphoreSlim _searches;
    private readonly SemaphoreSlim _worker = new(1, 1);
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, UserGate> _users = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="EngineSlots"/> class.
    /// </summary>
    /// <param name="searches">The search slots (<c>MaxConcurrentSearches</c>, read at startup).</param>
    /// <param name="wait">How long a search waits for one.</param>
    public EngineSlots(int searches, TimeSpan wait)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(searches, 1);
        Searches = searches;
        Wait = wait;
        _searches = new SemaphoreSlim(searches, searches);
    }

    /// <summary>Gets the number of search slots.</summary>
    public int Searches { get; }

    /// <summary>Gets how long a search waits for a slot.</summary>
    public TimeSpan Wait { get; }

    /// <summary>Gets the free search slots.</summary>
    public int FreeSearchSlots => _searches.CurrentCount;

    /// <summary>Gets the users who hold or wait for their own slot.</summary>
    public int UsersWithSlots
    {
        get
        {
            lock (_lock)
            {
                return _users.Count;
            }
        }
    }

    /// <summary>Takes a slot for an evaluation.</summary>
    /// <param name="purpose">What the evaluation is for: auto-collections take the worker's slot, and wait for it.</param>
    /// <param name="userId">The user the search or preview runs for: they get one slot at a time. <c>null</c> for none.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The slot, released when disposed.</returns>
    /// <exception cref="SmartSearchRefusedException">No slot came free in time (<c>Busy</c>).</exception>
    public async Task<IDisposable> EnterAsync(EvaluationPurpose purpose, Guid? userId, CancellationToken cancellationToken)
    {
        if (purpose == EvaluationPurpose.AutoCollection)
        {
            await _worker.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new Slot(_worker, null);
        }

        var started = Stopwatch.GetTimestamp();
        Slot? own = null;
        if (userId is { } id)
        {
            var gate = Acquire(id);
            var entered = false;
            try
            {
                entered = await gate.Semaphore.WaitAsync(Wait, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (!entered)
                {
                    Release(id, gate);
                }
            }

            if (!entered)
            {
                throw SmartSearchRefusedException.Busy();
            }

            own = new Slot(gate.Semaphore, () => Release(id, gate));
        }

        try
        {
            var left = Wait - Stopwatch.GetElapsedTime(started);
            if (!await _searches.WaitAsync(left > TimeSpan.Zero ? left : TimeSpan.Zero, cancellationToken).ConfigureAwait(false))
            {
                throw SmartSearchRefusedException.Busy();
            }
        }
        catch
        {
            own?.Dispose();
            throw;
        }

        return new Slot(_searches, null, own);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _searches.Dispose();
        _worker.Dispose();
    }

    private UserGate Acquire(Guid userId)
    {
        lock (_lock)
        {
            if (!_users.TryGetValue(userId, out var gate))
            {
                gate = new UserGate();
                _users[userId] = gate;
            }

            gate.References++;
            return gate;
        }
    }

    // Drops a holder or waiter; the user's gate goes when nobody holds or waits for it.
    private void Release(Guid userId, UserGate gate)
    {
        lock (_lock)
        {
            if (--gate.References == 0)
            {
                _users.Remove(userId);
                gate.Semaphore.Dispose();
            }
        }
    }

    private sealed class UserGate
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int References { get; set; }
    }

    // Releases its semaphore once, then the user's own slot (and its gate) when it has one.
    private sealed class Slot(SemaphoreSlim semaphore, Action? released, Slot? inner = null) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                semaphore.Release();
                released?.Invoke();
                inner?.Dispose();
            }
        }
    }
}
