using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Kit.TestSupport.Time;

/// <summary>
/// A clock whose timers fire only when it is moved on (<see cref="Advance"/>), so <c>Task.Delay(delay, clock)</c> and
/// <c>TimeProvider</c> timers complete exactly when the test says: Bookmarks' free-space watchdog, the kit's
/// startup gate, background-service restarts and library-change debounce. <see cref="ManualTimeProvider"/> moves
/// the time but leaves its timers on the system clock. Lifted from Bookmarks' tests.
/// </summary>
public sealed class ManualTimerProvider : TimeProvider
{
    private readonly object _lock = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now;

    public ManualTimerProvider()
        : this(new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero))
    {
    }

    public ManualTimerProvider(DateTimeOffset start)
    {
        _now = start;
    }

    /// <summary>Gets how many timers are armed (created, not disposed, with a due time).</summary>
    public int ArmedTimers
    {
        get
        {
            lock (_lock)
            {
                return _timers.Count(t => t.Due is not null);
            }
        }
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_lock)
        {
            return _now;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_lock)
        {
            _timers.Add(timer);
        }

        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>
    /// Waits until at least this many timers are armed (a watchdog has started), for up to 10 seconds.
    /// </summary>
    public async Task WaitForArmedTimersAsync(int count = 1)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (ArmedTimers < count)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"{ArmedTimers} timers armed, expected {count}.");
            }

            await Task.Delay(10);
        }
    }

    /// <summary>
    /// Moves the clock on, firing every timer that falls due on the way, in order (a periodic one as often as it falls due).
    /// </summary>
    public void Advance(TimeSpan by)
    {
        DateTimeOffset target;
        lock (_lock)
        {
            target = _now + by;
        }

        while (true)
        {
            ManualTimer? next;
            lock (_lock)
            {
                next = _timers.Where(t => t.Due is { } due && due <= target).OrderBy(t => t.Due).FirstOrDefault();
                if (next is null)
                {
                    _now = target;
                    return;
                }

                _now = next.Due!.Value;
                next.Due = next.Period is { } period ? _now + period : null;
            }

            next.Fire();
        }
    }

    private void Remove(ManualTimer timer)
    {
        lock (_lock)
        {
            _timers.Remove(timer);
        }
    }

    private sealed class ManualTimer(ManualTimerProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? Due { get; set; }

        public TimeSpan? Period { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._lock)
            {
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
                Period = period == Timeout.InfiniteTimeSpan || period == TimeSpan.Zero ? null : period;
            }

            return true;
        }

        public void Fire() => callback(state);

        public void Dispose() => owner.Remove(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
