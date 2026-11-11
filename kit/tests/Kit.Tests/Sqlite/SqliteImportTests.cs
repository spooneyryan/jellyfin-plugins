using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Sqlite;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.Kit.Tests.TestHosting;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jellyfin.Plugin.Kit.Tests.Sqlite;

/// <summary>
/// Export and import for a <see cref="SqliteDatabase"/>: <c>ExportData</c> into the kit's envelope,
/// and <see cref="SqliteStoreImport"/>: a dry run that is the import rolled back, Merge and Replace after a pre-operation
/// snapshot, users mapped and unknown ones skipped, and the refusals (another plugin, a newer schema, a read-only
/// database).
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class SqliteImportTests : IDisposable
{
    private static readonly Guid Alex = Guid.Parse("9fa5c677-8cf7-4a94-bff0-c82a60581ccf");
    private static readonly Guid Viewer = Guid.Parse("0038ee0f-9abc-4d55-9ef5-abcf8520c9bf");
    private static readonly Guid Gone = Guid.Parse("bcd54fb1-0000-4000-8000-000000000009");

    private static readonly IReadOnlyList<SqliteMigration> Schema =
    [
        new SqliteMigration(1, "create Notes", "CREATE TABLE Notes (Id TEXT PRIMARY KEY, UserId TEXT NOT NULL, Text TEXT NOT NULL);")
    ];

    private readonly SqliteFixture _fx = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _fx.Dispose();

    [Fact]
    public async Task ADryRun_IsTheImportRolledBack_WithItsCounts()
    {
        var database = await DatabaseWithAsync(("n1", Alex, "mine"));

        var report = await RunAsync(database, Envelope(("n1", Alex, "edited"), ("n2", Alex, "new"), ("n3", Gone, "theirs")), ImportRequest.Parse(null, null, null));

        Assert.True(report.DryRun);
        Assert.Equal(new ImportCounts { Incoming = 2, Added = 1, Updated = 1, SkippedUnknownUser = 1 }, report.Records);
        Assert.Null(report.SafetySnapshot);
        Assert.Equal([Gone], report.UnknownUsers.Select(u => u.Id));
        Assert.Equal(["n1|mine"], Notes(database));
        Assert.Empty(database.ListSnapshots());
        Assert.Equal(1, database.Generation);
    }

    [Fact]
    public async Task Merge_TakesAPreOperationSnapshot_ThenCommits()
    {
        var database = await DatabaseWithAsync(("n1", Alex, "mine"));

        var report = await RunAsync(database, Envelope(("n1", Alex, "edited"), ("n2", Viewer, "new")), ImportRequest.Parse("Merge", false, null));

        Assert.False(report.DryRun);
        Assert.Equal(new ImportCounts { Incoming = 2, Added = 1, Updated = 1 }, report.Records);
        Assert.StartsWith("userratings-import-", report.SafetySnapshot, StringComparison.Ordinal);
        Assert.Equal(["n1|edited", "n2|new"], Notes(database));
        var safety = Assert.Single(database.ListSnapshots());
        Assert.Equal((SnapshotKind.PreOp, SnapshotOperations.Import), (safety.Kind, safety.Operation));
        Assert.Equal(1, SqliteFixture.FileRows(System.IO.Path.Combine(_fx.Snapshots, "pre-op", safety.Name), "Notes"));
    }

    [Fact]
    public async Task AnImport_WaitsForAWriteInProgress_AndItsSafetySnapshotHoldsThatWrite()
    {
        // The pre-operation snapshot is taken in the same hold of the writer lock as the import, so a write that
        // commits while the import starts is in the safety snapshot, not in neither.
        var database = await DatabaseWithAsync(("n1", Alex, "mine"));
        using var inside = new ManualResetEventSlim();
        using var go = new ManualResetEventSlim();
        var write = Task.Run(
            () => database.WriteAsync(
                (c, t) =>
                {
                    Insert(c, t, "n5", Viewer, "written while the import starts");
                    inside.Set();
                    go.Wait(Ct);
                    return 0;
                },
                Ct),
            Ct);
        inside.Wait(Ct);

        var import = Task.Run(() => RunAsync(database, Envelope(("n2", Alex, "only")), ImportRequest.Parse("Replace", false, null)), Ct);
        await Task.Delay(300, Ct);

        Assert.False(import.IsCompleted);
        Assert.Empty(database.ListSnapshots());
        go.Set();
        await write;
        var report = await import;

        Assert.Equal(new ImportCounts { Incoming = 1, Added = 1, Removed = 2 }, report.Records);
        Assert.Equal(["n2|only"], Notes(database));
        var safety = System.IO.Path.Combine(_fx.Snapshots, "pre-op", report.SafetySnapshot!);
        Assert.Equal(2, SqliteFixture.FileRows(safety, "Notes"));
    }

    [Fact]
    public async Task Replace_LeavesOnlyTheExportsRecords()
    {
        var database = await DatabaseWithAsync(("n1", Alex, "mine"), ("n9", Viewer, "old"));

        var report = await RunAsync(database, Envelope(("n2", Alex, "only")), ImportRequest.Parse("Replace", false, null));

        Assert.Equal(new ImportCounts { Incoming = 1, Added = 1, Removed = 2 }, report.Records);
        Assert.Equal(["n2|only"], Notes(database));
    }

    [Fact]
    public async Task TheImport_RefusesAnotherPluginsExport_ANewerSchema_AndAReadOnlyDatabase()
    {
        var database = await DatabaseWithAsync(("n1", Alex, "mine"));

        Assert.Equal(PluginErrorKind.Invalid, (await Assert.ThrowsAsync<PluginException>(() => RunAsync(database, Envelope() with { PluginId = Guid.NewGuid() }, ImportRequest.Parse(null, null, null)))).Kind);
        Assert.Equal(PluginErrorKind.Conflict, (await Assert.ThrowsAsync<PluginException>(() => RunAsync(database, Envelope() with { SchemaVersion = 2 }, ImportRequest.Parse(null, null, null)))).Kind);

        var root = database.Read(c => SqliteFixture.Scalar(c, "SELECT rootpage FROM sqlite_master WHERE name = 'Notes';"));
        database.Release("test");
        SqliteFixture.CorruptPage(database.FilePath, root);
        Assert.False(database.QuickCheck()!.Ok);
        var refused = await Assert.ThrowsAsync<PluginException>(() => RunAsync(database, Envelope(("n2", Alex, "x")), ImportRequest.Parse(null, false, null)));
        Assert.Equal(PluginErrorKind.Conflict, refused.Kind);
        Assert.Empty(database.ListSnapshots());
    }

    [Fact]
    public async Task AnExport_ReadBackIntoAnotherDatabase_GivesTheSameRows()
    {
        var source = await DatabaseWithAsync(("n1", Alex, "Zoë – 星 ✓"), ("n2", Viewer, "two"));
        var envelope = ExportEnvelope.Create(TestKit.Options, source.ExportData(ReadNotes), [new ExportUser(Alex, "alex"), new ExportUser(Viewer, "viewer")]);
        var parsed = ExportEnvelope.Parse(envelope.ToUtf8Bytes());
        var target = _fx.Open(Schema, _fx.Directory.Combine("other", "userratings.db"));

        var report = await RunAsync(target, parsed, ImportRequest.Parse("Merge", false, null));

        Assert.Equal(1, parsed.SchemaVersion);
        Assert.Equal(2, report.Records.Added);
        Assert.Equal(Notes(source), Notes(target));
    }

    private static JsonObject ReadNotes(SqliteConnection connection, SqliteTransaction transaction)
    {
        var notes = new JsonArray();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT Id, UserId, Text FROM Notes ORDER BY Id;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            notes.Add(new JsonObject { ["Id"] = reader.GetString(0), ["UserId"] = reader.GetString(1), ["Text"] = reader.GetString(2) });
        }

        return new JsonObject { ["Notes"] = notes };
    }

    private static List<string> Notes(SqliteDatabase database)
        => database.Read(c =>
        {
            using var command = c.CreateCommand();
            command.CommandText = "SELECT Id, Text FROM Notes ORDER BY Id;";
            using var reader = command.ExecuteReader();
            var rows = new List<string>();
            while (reader.Read())
            {
                rows.Add(reader.GetString(0) + "|" + reader.GetString(1));
            }

            return rows;
        });

    private static ExportEnvelope Envelope(params (string Id, Guid User, string Text)[] notes) => new()
    {
        Plugin = "Kit Test",
        PluginId = TestKit.PluginId,
        PluginVersion = "1.0.0.0",
        SchemaVersion = 1,
        ExportedUtc = new DateTime(2026, 10, 19, 1, 0, 0, DateTimeKind.Utc),
        Users = [new ExportUser(Alex, "alex"), new ExportUser(Viewer, "viewer"), new ExportUser(Gone, "gone")],
        Data = new JsonObject
        {
            ["Notes"] = new JsonArray(notes.Select(n => (JsonNode)new JsonObject { ["Id"] = n.Id, ["UserId"] = n.User.ToString("N", CultureInfo.InvariantCulture), ["Text"] = n.Text }).ToArray())
        }
    };

    private static Task<ImportReport> RunAsync(SqliteDatabase database, ExportEnvelope envelope, ImportRequest request)
        => SqliteStoreImport.RunAsync(
            database,
            envelope,
            request,
            new NotesAdapter(),
            TestKit.Options,
            new Dictionary<Guid, string> { [Alex] = "alex", [Viewer] = "viewer" },
            new HashSet<Guid>(),
            new Jellyfin.Plugin.Kit.TestSupport.Logging.ListLogger(),
            Ct);

    private async Task<SqliteDatabase> DatabaseWithAsync(params (string Id, Guid User, string Text)[] notes)
    {
        var database = _fx.Open(Schema);
        await database.WriteAsync(
            (c, t) =>
            {
                foreach (var note in notes)
                {
                    Insert(c, t, note.Id, note.User, note.Text);
                }

                return 0;
            },
            Ct);
        return database;
    }

    private static void Insert(SqliteConnection connection, SqliteTransaction transaction, string id, Guid user, string text)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO Notes (Id, UserId, Text) VALUES ($id, $user, $text) ON CONFLICT (Id) DO UPDATE SET UserId = excluded.UserId, Text = excluded.Text;";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$user", user.ToString("N", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$text", text);
        command.ExecuteNonQuery();
    }

    // The plugin's part, as a SQLite plugin writes it: rows of mapped users, upserted (Merge) or the table replaced.
    private sealed class NotesAdapter : SqliteImportAdapter
    {
        public override IReadOnlyDictionary<Guid, int> CountRecordsByUser(JsonObject data, int schemaVersion)
            => Rows(data).GroupBy(n => n.User).ToDictionary(g => g.Key, g => g.Count());

        public override SqliteImportApplied Apply(SqliteConnection connection, SqliteTransaction transaction, JsonObject data, int schemaVersion, ImportMode mode, ImportUserMap users)
        {
            var existing = new Dictionary<string, string>(StringComparer.Ordinal);
            using (var read = connection.CreateCommand())
            {
                read.Transaction = transaction;
                read.CommandText = "SELECT Id, Text FROM Notes;";
                using var reader = read.ExecuteReader();
                while (reader.Read())
                {
                    existing[reader.GetString(0)] = reader.GetString(1);
                }
            }

            var incoming = Rows(data).Where(n => users.TryMap(n.User, out _)).ToList();
            var removed = 0;
            if (mode == ImportMode.Replace)
            {
                using var delete = connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM Notes;";
                removed = delete.ExecuteNonQuery() - incoming.Count(n => existing.ContainsKey(n.Id));
            }

            var added = 0;
            var updated = 0;
            var unchanged = 0;
            foreach (var note in incoming)
            {
                users.TryMap(note.User, out var user);
                Insert(connection, transaction, note.Id, user, note.Text);
                if (!existing.TryGetValue(note.Id, out var text))
                {
                    added++;
                }
                else if (string.Equals(text, note.Text, StringComparison.Ordinal))
                {
                    unchanged++;
                }
                else
                {
                    updated++;
                }
            }

            return new SqliteImportApplied(new ImportCounts { Incoming = incoming.Count, Added = added, Updated = updated, Unchanged = unchanged, Removed = removed }, []);
        }

        private static IEnumerable<(string Id, Guid User, string Text)> Rows(JsonObject data)
            => data["Notes"]!.AsArray().Select(n => ((string)n!["Id"]!, Guid.Parse((string)n["UserId"]!), (string)n["Text"]!));
    }
}
