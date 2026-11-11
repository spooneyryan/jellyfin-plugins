using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.UserRatings.Import;

/// <summary>
/// The rows of an export an import leaves out, with the reason for each: every one is counted
/// (the report's <c>Unusable</c>); the first <see cref="MaxListed"/> are listed (<c>UnusableRows</c>), so a damaged file of
/// a million rows doesn't make a report of a million lines.
/// </summary>
internal sealed class ImportUnusableRows
{
    /// <summary>How many rows the report lists.</summary>
    public const int MaxListed = 100;

    private readonly List<Entry> _rows = [];

    /// <summary>Gets how many rows were left out.</summary>
    public int Count { get; private set; }

    /// <summary>Gets the first <see cref="MaxListed"/> of them.</summary>
    public IReadOnlyList<Entry> Listed => _rows;

    /// <summary>Leaves a row out.</summary>
    /// <param name="table">Its table in the file (<c>Ratings</c>, <c>Archive</c>, …).</param>
    /// <param name="index">Its index in that table, from 0.</param>
    /// <param name="node">The row as in the file, for its id.</param>
    /// <param name="idProperty">The property of its id.</param>
    /// <param name="reason">Why.</param>
    public void Add(string table, int index, JsonNode? node, string idProperty, string reason)
    {
        Count++;
        if (_rows.Count < MaxListed)
        {
            var id = node is JsonObject row && row[idProperty] is JsonValue value && value.TryGetValue<string>(out var text) ? ImportRowRules.Shown(text) : null;
            _rows.Add(new Entry(table, index + 1, id, reason));
        }
    }

    /// <summary>A row left out.</summary>
    /// <param name="Table">Its table in the file.</param>
    /// <param name="Row">Its place in that table, from 1.</param>
    /// <param name="Id">Its id as in the file, if it has one.</param>
    /// <param name="Reason">Why it was left out.</param>
    internal sealed record Entry(string Table, int Row, string? Id, string Reason);
}
