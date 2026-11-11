using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.Kit.TestSupport.Storage;

/// <summary>
/// A plugin's store as <see cref="StoreConformanceTests"/> sees it. The plugin derives from
/// <see cref="StoreConformance{TDoc}"/>; this non-generic view exists because xunit test classes must be public while kit
/// types and plugin documents are internal, so the public test base can't be generic over the document.
/// </summary>
internal abstract class StoreConformanceSubject
{
    /// <summary>Gets the schema version the plugin's store writes.</summary>
    public abstract int CurrentVersion { get; }

    /// <summary>Gets the store file's name, for example <c>bookmarks.json</c>.</summary>
    public abstract string StoreFileName { get; }

    /// <summary>Gets real or synthetic files the store must load: earlier releases' files and older schema versions.</summary>
    public abstract IReadOnlyList<StoreFixture> Fixtures { get; }

    /// <summary>Gets the <c>FromVersion</c> of each of the plugin's migrations.</summary>
    public abstract IReadOnlyList<int> MigrationSources { get; }

    /// <summary>Opens the plugin's store over a file, optionally one version ahead with an extra migration.</summary>
    /// <param name="file">The store file.</param>
    /// <param name="options">Test variations.</param>
    /// <returns>The store.</returns>
    public abstract StoreHandle Open(string file, StoreOpenOptions? options = null);

    /// <summary>Serializes a current-version document with the given number of records, as the store writes it.</summary>
    /// <param name="records">The number of records.</param>
    /// <returns>The file's bytes.</returns>
    public abstract byte[] CreateFile(int records);

    /// <summary>Creates a current-version file of at least the given size.</summary>
    /// <param name="minimumBytes">The size to reach.</param>
    /// <returns>The file's bytes and its number of records.</returns>
    public abstract (byte[] Content, int Records) CreateLargeFile(long minimumBytes);

    /// <summary>Changes a current-version file so that it no longer deserializes (the version stays).</summary>
    /// <param name="root">The file's root object.</param>
    public abstract void BreakDeserialization(JsonObject root);

    /// <summary>
    /// Adds an unknown field to a stored record. Every plugin implements it: the kit's store rules give main records
    /// <c>[JsonExtensionData] Extra</c>, and a store whose records can't keep unknown fields returns <c>false</c> and says
    /// why in a comment, so the gap is a decision rather than an oversight.
    /// </summary>
    /// <param name="root">The file's root object, with at least one record.</param>
    /// <param name="name">The field name.</param>
    /// <param name="value">The field value.</param>
    /// <returns><c>true</c> once the field is added; <c>false</c> when the records have no extension data (the check then reports as skipped).</returns>
    public abstract bool AddUnknownRecordField(JsonObject root, string name, JsonNode value);
}

/// <summary>
/// The conformance definition of one plugin store. Derive from it in the plugin's tests and return it from
/// <see cref="StoreConformanceTests"/>' <c>CreateSubject</c>. Only the document hooks are required; the store is created
/// with the plugin's migrations and serializer options, or by <see cref="CreateStore"/> when the plugin overrides it.
/// </summary>
/// <typeparam name="TDoc">The plugin's document type.</typeparam>
internal abstract class StoreConformance<TDoc> : StoreConformanceSubject
    where TDoc : class, IVersionedDocument
{
    /// <inheritdoc />
    public override string StoreFileName => "store.json";

    /// <inheritdoc />
    public override IReadOnlyList<StoreFixture> Fixtures => [];

    /// <summary>Gets the plugin's migrations.</summary>
    public virtual IReadOnlyList<IJsonMigration> Migrations => [];

    /// <summary>Gets the plugin's serializer options.</summary>
    public virtual JsonSerializerOptions SerializerOptions => JsonStoreFormat.Options;

    /// <inheritdoc />
    public override IReadOnlyList<int> MigrationSources => Migrations.Select(m => m.FromVersion).ToList();

    /// <summary>Creates the empty document, as the plugin's store does.</summary>
    /// <returns>The document.</returns>
    public abstract TDoc CreateEmpty();

    /// <summary>Returns a new document with one more record; <paramref name="number"/> makes it distinct.</summary>
    /// <param name="document">The current document.</param>
    /// <param name="number">A number unique within the test.</param>
    /// <returns>The new document (never the same instance).</returns>
    public abstract TDoc AddRecord(TDoc document, int number);

    /// <summary>Counts a document's records.</summary>
    /// <param name="document">The document.</param>
    /// <returns>The number of records.</returns>
    public abstract int CountRecords(TDoc document);

    /// <summary>Creates a document with the given number of records. Override when adding one at a time is slow.</summary>
    /// <param name="records">The number of records.</param>
    /// <returns>The document.</returns>
    public virtual TDoc CreateDocument(int records)
    {
        var document = CreateEmpty();
        for (var i = 0; i < records; i++)
        {
            document = AddRecord(document, i);
        }

        return document;
    }

    /// <summary>Creates the store exactly as the plugin does, except for the version and migrations under test.</summary>
    /// <param name="file">The store file.</param>
    /// <param name="currentVersion">The schema version.</param>
    /// <param name="createEmpty">Creates the empty document at <paramref name="currentVersion"/>.</param>
    /// <param name="migrations">The migrations.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="timeProvider">The clock.</param>
    /// <returns>The store.</returns>
    public virtual JsonDocumentStore<TDoc> CreateStore(
        string file,
        int currentVersion,
        Func<TDoc> createEmpty,
        IReadOnlyList<IJsonMigration> migrations,
        ILogger logger,
        TimeProvider timeProvider)
        => new(file, currentVersion, createEmpty, migrations, logger, SerializerOptions, timeProvider);

    /// <inheritdoc />
    public override StoreHandle Open(string file, StoreOpenOptions? options = null)
    {
        var version = CurrentVersion;
        var migrations = Migrations;
        Func<TDoc> createEmpty = CreateEmpty;
        if (options?.ExtraMigration is { } extra)
        {
            // One version ahead: the empty document says so too, through the file format.
            version++;
            migrations = [.. migrations, extra];
            createEmpty = () => WithVersion(CreateEmpty(), CurrentVersion + 1);
        }

        var store = CreateStore(file, version, createEmpty, migrations, options?.Logger ?? NullLogger.Instance, options?.Time ?? TimeProvider.System);
        return new Handle(this, store);
    }

    /// <summary>Returns a copy of a document at another version, through its JSON form.</summary>
    /// <param name="document">The document.</param>
    /// <param name="version">The version.</param>
    /// <returns>The copy.</returns>
    public TDoc WithVersion(TDoc document, int version)
    {
        var root = (JsonObject)JsonSerializer.SerializeToNode(document, SerializerOptions)!;
        root[JsonStoreFormat.VersionProperty] = version;
        return root.Deserialize<TDoc>(SerializerOptions)!;
    }

    /// <inheritdoc />
    public override byte[] CreateFile(int records) => JsonSerializer.SerializeToUtf8Bytes(CreateDocument(records), SerializerOptions);

    /// <inheritdoc />
    public override (byte[] Content, int Records) CreateLargeFile(long minimumBytes)
    {
        var empty = CreateFile(0).LongLength;
        var sample = CreateFile(200).LongLength;
        var perRecord = Math.Max(1, (sample - empty) / 200.0);
        var records = (int)Math.Ceiling((minimumBytes - empty) / perRecord * 1.01);
        while (true)
        {
            var content = CreateFile(records);
            if (content.LongLength >= minimumBytes)
            {
                return (content, records);
            }

            records += (int)Math.Ceiling((minimumBytes - content.LongLength) / perRecord) + 10;
        }
    }

    /// <summary>
    /// Replaces the first array or object property of the root (other than <c>Version</c>) with a number, which no
    /// collection or object property can read. Override for a more realistic break, such as an unknown enum value.
    /// </summary>
    /// <param name="root">The file's root object.</param>
    public override void BreakDeserialization(JsonObject root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var property = root.FirstOrDefault(p => p.Key != JsonStoreFormat.VersionProperty && p.Value is JsonArray or JsonObject);
        if (property.Key is null)
        {
            throw new InvalidOperationException("The document has no array or object property to break; override BreakDeserialization.");
        }

        root[property.Key] = 42;
    }

    private sealed class Handle : StoreHandle
    {
        private readonly StoreConformance<TDoc> _owner;
        private readonly JsonDocumentStore<TDoc> _store;

        public Handle(StoreConformance<TDoc> owner, JsonDocumentStore<TDoc> store)
        {
            _owner = owner;
            _store = store;
        }

        public override StoreStatus Status => _store.Status;

        public override bool IsReadOnly => _store.IsReadOnly;

        public override int? LoadedVersion => _store.LoadedVersion;

        public override int? MigratedFrom => _store.MigratedFrom;

        public override string? StatusMessage => _store.StatusMessage;

        public override int DocumentVersion => _store.Current.Version;

        public override DateTime? LastSnapshotUtc => _store.LastSnapshotUtc;

        public override int Count() => _owner.CountRecords(_store.Current);

        public override Task AddRecordAsync(int number, CancellationToken cancellationToken)
            => _store.UpdateAsync(document => _owner.AddRecord(document, number), cancellationToken);

        public override Task NoOpUpdateAsync(CancellationToken cancellationToken)
            => _store.UpdateAsync(document => document, cancellationToken);

        public override Task<string?> SnapshotAsync(string directory, int keep, CancellationToken cancellationToken)
            => _store.SnapshotAsync(directory, keep, cancellationToken);

        public override DateTime? KeepSnapshotsBeforeUtc => _store.KeepSnapshotsBeforeUtc;

        public override Task<string?> PreOpSnapshotAsync(string directory, string operation, CancellationToken cancellationToken)
            => _store.PreOpSnapshotAsync(directory, operation, cancellationToken);

        public override IReadOnlyList<StoreSnapshotInfo> ListSnapshots(string directory) => _store.ListSnapshots(directory);

        public override Task<SnapshotRestoreResult> RestoreSnapshotAsync(string directory, string name, CancellationToken cancellationToken)
            => _store.RestoreSnapshotAsync(directory, name, cancellationToken);

        public override bool CanRecover => _store.CanRecover;

        public override Task<StoreRecoveryResult> RecoverAsync(string directory, string? snapshotName, bool startEmpty, CancellationToken cancellationToken)
            => _store.RecoverAsync(directory, snapshotName, startEmpty, cancellationToken);

        public override Task WaitUntilWritableAsync(CancellationToken cancellationToken) => _store.WaitUntilWritableAsync(cancellationToken);

        public override StoreExportData ExportData() => _store.ExportData();

        public override int CountData(JsonObject data, int schemaVersion) => _owner.CountRecords(_store.ReadData(data, schemaVersion));

        public override int CountFile(string path)
        {
            using var reader = _owner.CreateStore(path, _owner.CurrentVersion, _owner.CreateEmpty, _owner.Migrations, NullLogger.Instance, TimeProvider.System);
            return _owner.CountRecords(reader.Current);
        }

        public override void Dispose() => _store.Dispose();
    }
}

/// <summary>
/// A <see cref="StoreConformanceSubject"/>'s store, without its document type.
/// </summary>
internal abstract class StoreHandle : IDisposable
{
    public abstract StoreStatus Status { get; }

    public abstract bool IsReadOnly { get; }

    public abstract int? LoadedVersion { get; }

    public abstract int? MigratedFrom { get; }

    public abstract string? StatusMessage { get; }

    public abstract int DocumentVersion { get; }

    public abstract DateTime? LastSnapshotUtc { get; }

    public abstract int Count();

    public abstract Task AddRecordAsync(int number, CancellationToken cancellationToken);

    public abstract Task NoOpUpdateAsync(CancellationToken cancellationToken);

    public abstract Task<string?> SnapshotAsync(string directory, int keep, CancellationToken cancellationToken);

    /// <summary>Gets the time before which daily snapshots are kept because the store started empty.</summary>
    public abstract DateTime? KeepSnapshotsBeforeUtc { get; }

    /// <summary>Takes a pre-operation snapshot.</summary>
    /// <param name="directory">The snapshot folder.</param>
    /// <param name="operation">The operation.</param>
    /// <param name="cancellationToken">Cancels the snapshot.</param>
    /// <returns>The path, or <c>null</c>.</returns>
    public abstract Task<string?> PreOpSnapshotAsync(string directory, string operation, CancellationToken cancellationToken);

    /// <summary>Lists the store's snapshots.</summary>
    /// <param name="directory">The snapshot folder.</param>
    /// <returns>The snapshots.</returns>
    public abstract IReadOnlyList<StoreSnapshotInfo> ListSnapshots(string directory);

    /// <summary>Restores a snapshot.</summary>
    /// <param name="directory">The snapshot folder.</param>
    /// <param name="name">The snapshot's name.</param>
    /// <param name="cancellationToken">Cancels the restore.</param>
    /// <returns>The result.</returns>
    public abstract Task<SnapshotRestoreResult> RestoreSnapshotAsync(string directory, string name, CancellationToken cancellationToken);

    /// <summary>Gets a value indicating whether an admin can recover the data (the data missing or unreadable).</summary>
    public abstract bool CanRecover { get; }

    /// <summary>An admin's restore of a chosen snapshot, or start empty, while the data is missing or unreadable.</summary>
    /// <param name="directory">The snapshot folder.</param>
    /// <param name="snapshotName">The snapshot's name, or <c>null</c>.</param>
    /// <param name="startEmpty">Start empty instead.</param>
    /// <param name="cancellationToken">Cancels the recovery.</param>
    /// <returns>The result.</returns>
    public abstract Task<StoreRecoveryResult> RecoverAsync(string directory, string? snapshotName, bool startEmpty, CancellationToken cancellationToken);

    /// <summary>Waits until the store can be written.</summary>
    /// <param name="cancellationToken">Ends the wait.</param>
    /// <returns>A task.</returns>
    public abstract Task WaitUntilWritableAsync(CancellationToken cancellationToken);

    /// <summary>Gets the document as an export's <c>Data</c>, with its schema version.</summary>
    /// <returns>The data.</returns>
    public abstract StoreExportData ExportData();

    /// <summary>Reads an export's <c>Data</c> at a schema version and counts its records.</summary>
    /// <param name="data">The data.</param>
    /// <param name="schemaVersion">Its version.</param>
    /// <returns>The number of records.</returns>
    public abstract int CountData(JsonObject data, int schemaVersion);

    /// <summary>Counts the records of another file of this store's schema, such as a snapshot, with a separate store.</summary>
    /// <param name="path">The file.</param>
    /// <returns>The number of records.</returns>
    public abstract int CountFile(string path);

    public abstract void Dispose();
}

/// <summary>
/// Variations of a store under test.
/// </summary>
/// <param name="ExtraMigration">A migration from the plugin's current version; the store then runs one version ahead.</param>
/// <param name="Logger">The logger; none by default.</param>
/// <param name="Time">The clock; the system clock by default.</param>
internal sealed record StoreOpenOptions(IJsonMigration? ExtraMigration = null, ILogger? Logger = null, TimeProvider? Time = null);

/// <summary>
/// A file a store must load unchanged (at the current version) or migrate (at an older one).
/// </summary>
/// <param name="Name">What the file is, for example <c>Bookmarks 1.0.0</c>.</param>
/// <param name="Content">The file's exact bytes.</param>
/// <param name="Version">Its schema version (1 when it has no <c>Version</c>).</param>
/// <param name="Records">How many records the store must find in it.</param>
internal sealed record StoreFixture(string Name, byte[] Content, int Version, int Records);
