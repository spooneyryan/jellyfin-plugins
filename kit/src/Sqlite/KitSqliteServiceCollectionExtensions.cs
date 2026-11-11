using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Diagnostics;
using Jellyfin.Plugin.Kit.Hosting;
using MediaBrowser.Controller;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kit.Sqlite;

/// <summary>
/// Registers a plugin's <see cref="SqliteDatabase"/>.
/// </summary>
internal static class KitSqliteServiceCollectionExtensions
{
    /// <summary>The default Diagnostics key of the database, the JSON store's.</summary>
    public const string DiagnosticsKey = "Store";

    /// <summary>The name of the maintenance step that runs <c>PRAGMA quick_check</c>.</summary>
    public const string IntegrityCheckStep = "IntegrityCheck";

    /// <summary>The name of the maintenance step that takes the daily snapshot.</summary>
    public const string SnapshotsStep = "Snapshots";

    /// <summary>The name of the maintenance step that checkpoints the write-ahead log.</summary>
    public const string CheckpointStep = "Checkpoint";

    /// <summary>
    /// Registers the plugin's database <c>&lt;data&gt;/&lt;folder&gt;/&lt;fileName&gt;</c> as a singleton
    /// <see cref="SqliteDatabase"/> (snapshots in <c>&lt;data&gt;/&lt;folder&gt;/snapshots</c>, logging under
    /// <c>Jellyfin.Plugin.&lt;X&gt;.Kit.Storage</c>), its <see cref="SqliteDatabaseService"/> (opened after the startup
    /// gate, released at shutdown), its Diagnostics entry, and three steps of the daily maintenance: <c>PRAGMA
    /// quick_check</c> first (<see cref="KitMaintenanceOrder.IntegrityCheck"/>), then the daily snapshot
    /// (<see cref="KitMaintenanceOrder.Snapshots"/>), and last a checkpoint of the write-ahead log
    /// (<see cref="KitMaintenanceOrder.Checkpoint"/>, <see cref="SqliteDatabase.CheckpointAsync"/>): the backstop of
    /// the checkpoints asked for since the last run, one whose tries ran out among them. The steps run when the plugin
    /// also calls <c>AddKitMaintenance</c> and declares its maintenance task. Call it after <c>AddKit</c>, only when
    /// <c>KitAbiGuard.Register</c> returned <c>true</c>. It only adds descriptors and touches no SQLite type, so it
    /// never throws in <c>RegisterServices</c> and a build that can't bind Microsoft.Data.Sqlite never stops the
    /// server.
    /// </summary>
    /// <param name="services">The server's service collection.</param>
    /// <param name="fileName">The database's file name, for example <c>userratings.db</c>.</param>
    /// <param name="migrations">The schema steps.</param>
    /// <param name="snapshotsToKeep">How many daily snapshots to keep, read at each run (<c>SnapshotsToKeep</c>).</param>
    /// <param name="diagnosticsKey">The Diagnostics key; <see cref="DiagnosticsKey"/> by default.</param>
    /// <param name="preOpKeepWithin">Keeps the pre-operation snapshots by age instead: those taken within this time,
    /// none older, also at each daily snapshot (<see cref="SqliteDatabase.PreOpKeepWithin"/>); null, the default, keeps
    /// the newest five.</param>
    /// <param name="secureDelete">Whether deleted content is overwritten with zeros
    /// (<see cref="SqliteDatabase.SecureDelete"/>, <c>PRAGMA secure_delete</c>); on by default, <c>false</c> opts
    /// out.</param>
    /// <param name="preOpCopies">The files whose pre-operation copies the daily snapshot also deletes past
    /// <paramref name="preOpKeepWithin"/> (<see cref="SqliteDatabase.PreOpCopies"/>), such as the kit's archive's
    /// <c>userratings-archived.jsonl</c>; none by default.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddKitSqliteDatabase(
        this IServiceCollection services,
        string fileName,
        IReadOnlyList<SqliteMigration> migrations,
        Func<IServiceProvider, int> snapshotsToKeep,
        string diagnosticsKey = DiagnosticsKey,
        TimeSpan? preOpKeepWithin = null,
        bool secureDelete = true,
        IReadOnlyList<string>? preOpCopies = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrEmpty(fileName);
        ArgumentNullException.ThrowIfNull(migrations);
        ArgumentNullException.ThrowIfNull(snapshotsToKeep);
        ArgumentException.ThrowIfNullOrWhiteSpace(diagnosticsKey);
        if (!string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal))
        {
            throw new ArgumentException("The database's file name must be a plain file name in the plugin's data folder.", nameof(fileName));
        }

        if (services.Any(d => d.ServiceType == typeof(SqliteDatabase)))
        {
            return services;
        }

        services.AddSingleton(provider =>
        {
            var options = provider.GetRequiredService<KitOptions>();
            var paths = provider.GetRequiredService<KitPaths>();
            return new SqliteDatabase(
                Path.Combine(paths.Root, fileName),
                migrations,
                options.CreateLogger(provider.GetRequiredService<ILoggerFactory>(), "Storage"),
                paths.Snapshots,
                options.Clock(provider))
            {
                PreOpKeepWithin = preOpKeepWithin,
                SecureDelete = secureDelete,
                PreOpCopies = preOpCopies ?? []
            };
        });
        services.AddHostedService(provider =>
        {
            var options = provider.GetRequiredService<KitOptions>();
            return new SqliteDatabaseService(
                provider.GetRequiredService<IServerApplicationHost>(),
                provider.GetRequiredService<SqliteDatabase>(),
                options.CreateLogger(provider.GetRequiredService<ILoggerFactory>(), "Storage"),
                options.Clock(provider));
        });
        services.AddKitDiagnosticsSource(diagnosticsKey, static provider => provider.GetRequiredService<SqliteDatabase>().Describe());
        services.AddKitMaintenanceStep(IntegrityCheckStep, KitMaintenanceOrder.IntegrityCheck, static (provider, _) =>
        {
            var database = provider.GetRequiredService<SqliteDatabase>();
            var check = database.QuickCheck();
            IDictionary<string, object?> result = check is null
                ? new Dictionary<string, object?>(StringComparer.Ordinal) { ["Skipped"] = "The database can't be opened.", ["Status"] = database.Status.ToString() }
                : check.ToDictionary();
            return Task.FromResult<IDictionary<string, object?>?>(result);
        });
        services.AddKitMaintenanceStep(SnapshotsStep, KitMaintenanceOrder.Snapshots, async (provider, cancellationToken) =>
        {
            var database = provider.GetRequiredService<SqliteDatabase>();
            var path = await database.SnapshotAsync(Math.Max(0, snapshotsToKeep(provider)), cancellationToken).ConfigureAwait(false);
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["Snapshot"] = path is null ? null : Path.GetFileName(path),
                ["Status"] = database.Status.ToString()
            };
        });
        services.AddKitMaintenanceStep(CheckpointStep, KitMaintenanceOrder.Checkpoint, static async (provider, cancellationToken) =>
        {
            var database = provider.GetRequiredService<SqliteDatabase>();
            var complete = await database.CheckpointAsync(cancellationToken).ConfigureAwait(false);
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["Complete"] = complete,
                ["Pending"] = database.CheckpointPending,
                ["Status"] = database.Status.ToString()
            };
        });
        return services;
    }
}
