namespace Jellyfin.Plugin.SmartSearch.AutoCollections;

/// <summary>How collections are written.</summary>
internal enum CollectionWriterKind
{
    /// <summary>The links replaced in one save (<see cref="DirectReplaceWriter"/>).</summary>
    DirectReplace,

    /// <summary>The server's own add and remove calls (<see cref="ManagerApiWriter"/>).</summary>
    ManagerApi
}
