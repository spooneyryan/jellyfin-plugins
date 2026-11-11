using System;
using System.Threading;

namespace Jellyfin.Plugin.LibraryMaintenance.Operations;

/// <summary>
/// The operation lock of <see cref="LibraryOperationGate"/>, held until disposed (once; a second dispose does nothing).
/// </summary>
internal sealed class OperationLease : IDisposable
{
    private Action? _release;

    /// <summary>
    /// Initializes a new instance of the <see cref="OperationLease"/> class.
    /// </summary>
    /// <param name="operation">What holds the lock.</param>
    /// <param name="release">Releases it.</param>
    public OperationLease(string operation, Action release)
    {
        Operation = operation;
        _release = release;
    }

    /// <summary>Gets what holds the lock (a job id and phase, for Diagnostics and logs).</summary>
    public string Operation { get; }

    /// <inheritdoc />
    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}
