namespace Jellyfin.Plugin.Kit.Storage;

/// <summary>
/// A document kept by <see cref="JsonDocumentStore{TDoc}"/>: the whole state of one store file.
/// </summary>
/// <remarks>
/// The store treats a document as an immutable snapshot: readers share it without locks, and a change returns a new
/// instance (a record <c>with</c> expression) instead of editing the current one. The document's own <c>Version</c>
/// property is written as the file's <c>Version</c>, so its default must be the store's current schema version.
/// </remarks>
internal interface IVersionedDocument
{
    /// <summary>
    /// Gets the schema version of the document, the <c>Version</c> property of the file. Schema versions are independent
    /// of plugin versions; a file without one is version 1.
    /// </summary>
    int Version { get; }
}
