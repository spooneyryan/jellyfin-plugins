using System;
using System.Collections.Generic;
using System.Threading;

namespace Jellyfin.Plugin.AccessRoles.Enforcement;

/// <summary>
/// What Diagnostics shows of the settings filter (the Overview's <c>Filter errors</c> chip: failures are counted and shown):
/// the rules that failed and refused, the last such error, and the degraded snapshots seen.
/// </summary>
internal sealed class EnforcementStatus
{
    private readonly object _lock = new();
    private long _filterErrors;
    private string? _lastError;
    private DateTime? _lastErrorUtc;
    private string? _degradedBecause;

    /// <summary>Gets how many settings rules failed and refused (fail closed) in this process.</summary>
    public long FilterErrors => Interlocked.Read(ref _filterErrors);

    /// <summary>
    /// Records a settings rule that failed for one request.
    /// </summary>
    /// <param name="message">What failed, for the admin: no user name.</param>
    /// <param name="utc">When.</param>
    public void Error(string message, DateTime utc)
    {
        Interlocked.Increment(ref _filterErrors);
        lock (_lock)
        {
            _lastError = message;
            _lastErrorUtc = utc;
        }
    }

    /// <summary>
    /// Records the degraded state the last snapshot had (<see cref="LockSnapshot.DegradedBecause"/>).
    /// </summary>
    /// <param name="because">The store status, or <c>null</c>.</param>
    /// <returns><c>true</c> when it changed.</returns>
    public bool Degraded(string? because)
    {
        lock (_lock)
        {
            if (string.Equals(_degradedBecause, because, StringComparison.Ordinal))
            {
                return false;
            }

            _degradedBecause = because;
            return true;
        }
    }

    /// <summary>
    /// Describes it.
    /// </summary>
    /// <returns>The description.</returns>
    public IDictionary<string, object?> Describe()
    {
        lock (_lock)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["FilterErrors"] = FilterErrors,
                ["LastError"] = _lastError,
                ["LastErrorUtc"] = _lastErrorUtc,
                ["DegradedBecause"] = _degradedBecause
            };
        }
    }
}
