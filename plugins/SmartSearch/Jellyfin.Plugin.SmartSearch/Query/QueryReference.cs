using System;
using System.Text.Json;

namespace Jellyfin.Plugin.SmartSearch.Query;

/// <summary>
/// Reads a reference value <c>{Id, Name}</c> (<c>Series</c>, later <c>People</c> and collections) the one
/// way the validator, the canonicalizer and the evaluators all use: an object with <c>Id</c> (a non-empty GUID in any
/// format) and optionally <c>Name</c> (text of at most 200 characters, or null), each named once ignoring case, and
/// nothing else. So <c>{"Id": a, "id": b}</c> is refused, not read as <c>a</c> by one and <c>b</c> by another.
/// </summary>
internal static class QueryReference
{
    /// <summary>Reads a reference.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The reference, or what is wrong with it.</returns>
    public static QueryReferenceRead Read(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return QueryReferenceRead.Problem(string.Empty, "This must be a reference {Id, Name}.");
        }

        JsonElement? id = null;
        JsonElement? name = null;
        foreach (var property in value.EnumerateObject())
        {
            var isId = string.Equals(property.Name, "Id", StringComparison.OrdinalIgnoreCase);
            if (!isId && !string.Equals(property.Name, "Name", StringComparison.OrdinalIgnoreCase))
            {
                return QueryReferenceRead.Problem("." + property.Name, "A reference has only Id and Name.");
            }

            if ((isId ? id : name) is not null)
            {
                return QueryReferenceRead.Problem("." + property.Name, "A reference names Id and Name once each.");
            }

            if (isId)
            {
                id = property.Value;
            }
            else
            {
                name = property.Value;
            }
        }

        if (id is not { ValueKind: JsonValueKind.String } idText || !Guid.TryParse(idText.GetString(), out var guid) || guid == Guid.Empty)
        {
            return QueryReferenceRead.Problem(".Id", "A reference needs an id.");
        }

        if (name is { } n && n.ValueKind != JsonValueKind.Null
            && (n.ValueKind != JsonValueKind.String || n.GetString()!.Length > QueryLimits.MaxTextLength))
        {
            return QueryReferenceRead.Problem(".Name", "A reference's name is text of at most 200 characters.");
        }

        return new QueryReferenceRead(true, guid, name is { ValueKind: JsonValueKind.String } text ? text.GetString()!.Trim() : null, string.Empty, string.Empty);
    }
}
