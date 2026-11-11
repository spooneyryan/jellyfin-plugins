namespace Jellyfin.Plugin.Kit.Diagnostics;

/// <summary>
/// An entry of the plugin's own in the Diagnostics body (<see cref="KitDiagnosticsServiceCollectionExtensions.AddKitDiagnosticsSource"/>).
/// </summary>
internal interface IKitDiagnosticsSource
{
    /// <summary>Gets the key in the Diagnostics body.</summary>
    string Key { get; }

    /// <summary>Describes the entry now.</summary>
    /// <returns>The value.</returns>
    object? Describe();
}
