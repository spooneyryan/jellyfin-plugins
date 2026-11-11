using System.Collections.Generic;

namespace Jellyfin.Plugin.Kit.Storage;

/// <summary>
/// What a plugin's <see cref="JsonImportAdapter{TDoc}.Apply"/> made of the stored document and the export's.
/// </summary>
/// <typeparam name="TDoc">The store's document.</typeparam>
/// <param name="Document">The document to store; the stored instance itself when nothing changes.</param>
/// <param name="Counts">What changed.</param>
/// <param name="Notes">Sentences for the report, such as records skipped and why; never names or notes of users.</param>
internal sealed record ImportApplied<TDoc>(TDoc Document, ImportCounts Counts, IReadOnlyList<string> Notes)
    where TDoc : class, IVersionedDocument;
