namespace Jellyfin.Plugin.SmartSearch.AutoCollections;

/// <summary>What the worker is doing with a definition now, shown over its stored status.</summary>
/// <param name="Status"><c>Queued</c> or <c>Running</c>.</param>
/// <param name="Reason">Why it is queued that long (<see cref="AutoCollectionReasons.WaitingForScan"/>), or <c>null</c>.</param>
internal sealed record AutoCollectionActivity(SmartSearchAutoCollectionStatus Status, string? Reason = null)
{
    /// <summary>Gets the activity of a definition with a run asked for.</summary>
    public static AutoCollectionActivity Queued { get; } = new(SmartSearchAutoCollectionStatus.Queued);

    /// <summary>Gets the activity of a definition in a pass that waits for a library scan to end.</summary>
    public static AutoCollectionActivity WaitingForScan { get; } = new(SmartSearchAutoCollectionStatus.Queued, AutoCollectionReasons.WaitingForScan);

    /// <summary>Gets the activity of a definition being run.</summary>
    public static AutoCollectionActivity Running { get; } = new(SmartSearchAutoCollectionStatus.Running);
}
