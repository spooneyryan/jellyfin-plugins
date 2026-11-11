namespace Jellyfin.Plugin.Kit.Storage;

/// <summary>
/// How an import combines the export's records with the stored ones (<c>mode=</c>).
/// </summary>
internal enum ImportMode
{
    /// <summary>Records are matched by id: new ones are added, a newer copy replaces an older one, nothing is removed.</summary>
    Merge,

    /// <summary>The export's records replace the stored ones (after a snapshot).</summary>
    Replace
}
