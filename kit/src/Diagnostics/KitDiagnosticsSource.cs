using System;

namespace Jellyfin.Plugin.Kit.Diagnostics;

/// <summary>
/// An <see cref="IKitDiagnosticsSource"/> over a delegate.
/// </summary>
/// <param name="key">The key.</param>
/// <param name="describe">The delegate.</param>
internal sealed class KitDiagnosticsSource(string key, Func<object?> describe) : IKitDiagnosticsSource
{
    /// <inheritdoc />
    public string Key { get; } = key;

    /// <inheritdoc />
    public object? Describe() => describe();
}
