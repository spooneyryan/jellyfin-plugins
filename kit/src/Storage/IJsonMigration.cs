using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.Kit.Storage;

/// <summary>
/// One schema upgrade step of a <see cref="JsonDocumentStore{TDoc}"/> file, from <see cref="FromVersion"/> to
/// <see cref="FromVersion"/> + 1.
/// </summary>
/// <remarks>
/// <para>Steps run in order on the parsed file before it is deserialized. The store sets the root's <c>Version</c> after
/// each step, so a step only reshapes the data. A step may throw: the store then keeps the file byte-identical, serves an
/// empty document read-only (<see cref="StoreStatus.ReadOnlyMigrationFailed"/>) and logs the step, so no later write can
/// replace the user's data with an empty document.</para>
/// <para>A store's steps must cover every version from 1 to the current version minus 1, one step each.</para>
/// </remarks>
internal interface IJsonMigration
{
    /// <summary>
    /// Gets the schema version this step upgrades from.
    /// </summary>
    int FromVersion { get; }

    /// <summary>
    /// Reshapes the parsed file in place from <see cref="FromVersion"/> to the next version.
    /// </summary>
    /// <param name="root">The file's root object.</param>
    void Apply(JsonObject root);
}
