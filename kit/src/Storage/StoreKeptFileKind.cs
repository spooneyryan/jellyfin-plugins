namespace Jellyfin.Plugin.Kit.Storage;

/// <summary>
/// Why a store keeps a copy of its file (<see cref="StoreKeptFile"/>).
/// </summary>
internal enum StoreKeptFileKind
{
    /// <summary>The file as it was before an upgrade to a newer schema, <c>&lt;file&gt;.v&lt;N&gt;-&lt;ts&gt;</c>.</summary>
    PreUpgradeCopy,

    /// <summary>A file that couldn't be read and was moved aside, <c>&lt;file&gt;.corrupt-&lt;ts&gt;</c>.</summary>
    Corrupt
}
