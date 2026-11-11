using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SmartSearch.Api.Models;
using Jellyfin.Plugin.SmartSearch.Query;
using Jellyfin.Plugin.SmartSearch.Storage;
using Jellyfin.Plugin.SmartSearch.Tests.Planning;

namespace Jellyfin.Plugin.SmartSearch.Tests.SavedQueries;

/// <summary>Queries, requests and a reference guard for the saved-query tests.</summary>
internal static class SavedQueryTestData
{
    public const string Keep = """{"Field":"Overview","Op":"Contains","Value":"keep"}""";

    public const string AdminOnly = """{"Field":"Path","Op":"Contains","Value":"film"}""";

    public const string PerUser = """{"Field":"Played","Op":"Is","Value":false}""";

    public static string Q(string rule = Keep) => QueryPlannerTests.Q(rule);

    /// <summary>A save request as the body reader makes it.</summary>
    public static SmartSearchSavedQueryRequestDto Request(string name, string? query = null, bool shared = false, DateTime? ifUpdatedAt = null, string? description = null)
    {
        var read = QueryJson.Read(Encoding.UTF8.GetBytes(query ?? Q()));
        return new SmartSearchSavedQueryRequestDto
        {
            Name = name,
            Description = description,
            Shared = shared,
            Query = read.Definition,
            Read = read,
            IfUpdatedAt = ifUpdatedAt
        };
    }

    /// <summary>The JSON of a save request.</summary>
    public static string Body(string name, string? query = null, bool shared = false, DateTime? ifUpdatedAt = null, string extra = "")
        => "{\"Name\":" + JsonSerializer.Serialize(name) + ",\"Shared\":" + (shared ? "true" : "false") + ",\"Query\":" + (query ?? Q())
            + (ifUpdatedAt is { } at ? ",\"IfUpdatedAt\":\"" + at.ToString("O", System.Globalization.CultureInfo.InvariantCulture) + "\"" : string.Empty) + extra + "}";

    /// <summary>A record written straight to the store (a query of another version, a broken one).</summary>
    public static SavedQueryRecord Record(Guid owner, string name, string queryJson, bool shared = false, DateTime? at = null)
    {
        using var query = JsonDocument.Parse(queryJson);
        var when = at ?? new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        return new SavedQueryRecord
        {
            Id = Guid.NewGuid(),
            OwnerUserId = owner,
            Name = name,
            Shared = shared,
            Query = query.RootElement.Clone(),
            CreatedUtc = when,
            UpdatedUtc = when,
            UpdatedByUserId = owner
        };
    }

    public static Task AddAsync(SavedQueryStore store, params SavedQueryRecord[] records)
        => store.UpdateAsync(d => SavedQueryStore.WithQueries(d, [.. d.Queries, .. records]), CancellationToken.None);
}

/// <summary>A reference guard with references set by the test, and refusals on demand.</summary>
internal sealed class TestReferenceGuard : ISavedQueryReferenceGuard
{
    public HashSet<Guid> Referenced { get; } = [];

    /// <summary>Gets or sets the outcome of the next edit or delete; <c>Done</c> writes it.</summary>
    public SavedQueryGuardStatus Next { get; set; } = SavedQueryGuardStatus.Done;

    public List<SavedQueryEdit> Edits { get; } = [];

    public List<SavedQueryDeletion> Deletions { get; } = [];

    /// <summary>Gets how many bulk changes ran through the guard.</summary>
    public int BulkChanges { get; private set; }

    /// <summary>Gets the bulk changes the guard was asked about, and whether each was asked inside a bulk change.</summary>
    public List<(SavedQueryBulkChange Change, bool InsideBulk)> Checked { get; } = [];

    private bool InsideBulk { get; set; }

    /// <summary>Gets or sets a value indicating whether the references can't be checked.</summary>
    public bool ReferencesUnknown { get; set; }

    public int CountReferences(Guid savedQueryId) => Referenced.Contains(savedQueryId) ? 1 : 0;

    public IReadOnlyList<SmartSearchAutoCollectionRefDto> FindBrokenReferences(SavedQueryBulkChange change)
    {
        Checked.Add((change, InsideBulk));
        return change.Removed.Concat(change.Changed).Where(Referenced.Contains).Select(id => Ref(id, change.Removed.Contains(id) ? "Its query would be removed" : "Its query would change")).ToList();
    }

    public async Task<T> BulkChangeAsync<T>(Func<CancellationToken, Task<T>> write, CancellationToken cancellationToken)
    {
        BulkChanges++;
        InsideBulk = true;
        try
        {
            return await write(cancellationToken);
        }
        finally
        {
            InsideBulk = false;
        }
    }

    public async Task<SavedQueryGuardOutcome> UpdateAsync(SavedQueryEdit edit, Func<CancellationToken, Task> save, CancellationToken cancellationToken)
    {
        Edits.Add(edit);
        if (Next != SavedQueryGuardStatus.Done)
        {
            return Refusal(edit.Current.Id);
        }

        await save(cancellationToken);
        return Referenced.Contains(edit.Current.Id) ? new SavedQueryGuardOutcome(SavedQueryGuardStatus.Done, [Ref(edit.Current.Id, "Changed")]) : SavedQueryGuardOutcome.Done;
    }

    public async Task<SavedQueryGuardOutcome> DeleteAsync(SavedQueryDeletion deletion, Func<CancellationToken, Task> delete, CancellationToken cancellationToken)
    {
        Deletions.Add(deletion);
        if (Next != SavedQueryGuardStatus.Done)
        {
            return Refusal(deletion.Current.Id);
        }

        if (Referenced.Contains(deletion.Current.Id) && !deletion.DetachReferences)
        {
            return new SavedQueryGuardOutcome(SavedQueryGuardStatus.Referenced, [Ref(deletion.Current.Id, "Uses it")]);
        }

        await delete(cancellationToken);
        Referenced.Remove(deletion.Current.Id);
        return SavedQueryGuardOutcome.Done;
    }

    private SavedQueryGuardOutcome Refusal(Guid id) => new(Next, [Ref(id, "Refused")]);

    private static SmartSearchAutoCollectionRefDto Ref(Guid id, string message)
        => new() { Id = new Guid(id.ToByteArray().Reverse().ToArray()), Name = "Collection of " + id.ToString("N")[..4], Messages = [message] };
}
