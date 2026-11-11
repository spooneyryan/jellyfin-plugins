using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Hosting;
using MediaBrowser.Controller;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kit.Sqlite;

/// <summary>
/// Opens a <see cref="SqliteDatabase"/> once the server has started (its integrity check, a corruption restore, the
/// upgrade) and lets go of its file when the server stops (the pool cleared, the WAL checkpointed, only
/// <c>&lt;db&gt;.db</c> left). Registered by <c>AddKitSqliteDatabase</c>.
/// </summary>
/// <remarks>
/// It touches no Microsoft.Data.Sqlite type: the database's own members catch a binding failure and report
/// <see cref="SqliteDatabaseStatus.Unavailable"/>. The open runs after the startup gate (nothing heavy before it); a
/// request that comes earlier opens the database itself. An in-process restart keeps the process and its pools, which
/// is why the pool is cleared at every stop.
/// </remarks>
internal sealed class SqliteDatabaseService : KitBackgroundService
{
    private readonly SqliteDatabase _database;

    /// <summary>
    /// Initializes a new instance of the <see cref="SqliteDatabaseService"/> class.
    /// </summary>
    /// <param name="applicationHost">The server, for the startup gate.</param>
    /// <param name="database">The database.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="timeProvider">The clock of the gate.</param>
    public SqliteDatabaseService(IServerApplicationHost applicationHost, SqliteDatabase database, ILogger logger, TimeProvider? timeProvider = null)
        : base(applicationHost, logger, timeProvider)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    /// <inheritdoc />
    protected override bool RestartAfterFailure => false;

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            _database.Release("server stopping");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Could not release {File} while the server stops", _database.FilePath);
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override Task RunAsync(CancellationToken stoppingToken)
    {
        var status = _database.Open();
        if (status is SqliteDatabaseStatus.Ok or SqliteDatabaseStatus.Migrated)
        {
            Logger.LogInformation("Opened {File} (schema {Version}, {Status})", _database.FilePath, _database.SchemaVersion, status);
        }
        else
        {
            Logger.LogWarning("Opened {File}: {Status}. {Message}", _database.FilePath, status, _database.StatusMessage);
        }

        return Task.CompletedTask;
    }
}
