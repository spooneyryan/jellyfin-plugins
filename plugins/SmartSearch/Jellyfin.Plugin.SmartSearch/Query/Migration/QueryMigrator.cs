using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.SmartSearch.Query.Migration;

/// <summary>
/// Upgrades a query to the current schema version on read, step by step with <see cref="IQueryMigration"/>s
/// on the JSON tree, before it is deserialized. A query without <c>Version</c> is version 1; a newer one is left alone
/// and reported, so it is kept byte for byte and marked unsupported.
/// </summary>
internal sealed class QueryMigrator
{
    /// <summary>The query schema version this build writes.</summary>
    public const int CurrentVersion = 1;

    private readonly Dictionary<int, IQueryMigration> _steps;

    /// <summary>
    /// Initializes a new instance of the <see cref="QueryMigrator"/> class.
    /// </summary>
    /// <param name="steps">The steps: one from each version from 1 to <paramref name="currentVersion"/> - 1.</param>
    /// <param name="currentVersion">The version the steps lead to.</param>
    /// <exception cref="ArgumentException">A step is missing, repeats, or starts outside that range.</exception>
    public QueryMigrator(IEnumerable<IQueryMigration> steps, int currentVersion)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(currentVersion, 1);
        _steps = [];
        foreach (var step in steps)
        {
            if (step.FromVersion < 1 || step.FromVersion >= currentVersion || !_steps.TryAdd(step.FromVersion, step))
            {
                throw new ArgumentException("The migration from version " + step.FromVersion + " is out of range or repeats.", nameof(steps));
            }
        }

        var missing = Enumerable.Range(1, currentVersion - 1).Where(v => !_steps.ContainsKey(v)).ToList();
        if (missing.Count > 0)
        {
            throw new ArgumentException("No migration from version " + missing[0] + ".", nameof(steps));
        }

        Current = currentVersion;
    }

    /// <summary>Gets the migrator of this build: version <see cref="CurrentVersion"/>, no steps yet.</summary>
    public static QueryMigrator Default { get; } = new([], CurrentVersion);

    /// <summary>Gets the version the migrator leads to.</summary>
    public int Current { get; }

    /// <summary>
    /// Upgrades a query in place. The root's <c>Version</c> is matched case-insensitively, as the deserializer matches it.
    /// </summary>
    /// <param name="query">The query's root object.</param>
    /// <returns>What happened.</returns>
    public QueryMigrationResult Migrate(JsonObject query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var property = query.FirstOrDefault(p => string.Equals(p.Key, "Version", StringComparison.OrdinalIgnoreCase));
        int version;
        if (property.Key is null)
        {
            version = 1;
        }
        else if (property.Value is not JsonValue value || value.GetValueKind() != JsonValueKind.Number)
        {
            return new QueryMigrationResult(QueryMigrationStatus.Invalid, null);
        }
        else if (!value.TryGetValue(out version))
        {
            // Plain digits too many for an int are still a version, a newer one; 1.0, 1.5 or 1e0 is refused (the
            // model's Version is an int, written 1).
            return value.ToJsonString().All(char.IsAsciiDigit)
                ? new QueryMigrationResult(QueryMigrationStatus.Newer, null)
                : new QueryMigrationResult(QueryMigrationStatus.Invalid, null);
        }
        else if (version < 1)
        {
            return new QueryMigrationResult(QueryMigrationStatus.Invalid, null);
        }

        if (version > Current)
        {
            return new QueryMigrationResult(QueryMigrationStatus.Newer, version);
        }

        if (version == Current)
        {
            return new QueryMigrationResult(QueryMigrationStatus.UpToDate, version);
        }

        var from = version;
        for (; version < Current; version++)
        {
            _steps[version].Apply(query);
            SetVersion(query, version + 1);
        }

        return new QueryMigrationResult(QueryMigrationStatus.Migrated, from);
    }

    private static void SetVersion(JsonObject query, int version)
    {
        foreach (var key in query.Select(p => p.Key).Where(k => string.Equals(k, "Version", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            query.Remove(key);
        }

        query["Version"] = version;
    }
}
