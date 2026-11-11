namespace Jellyfin.Plugin.SmartSearch.Query.Fields;

/// <summary>
/// What each entry of a rule's <c>Values</c> is for a field.
/// </summary>
internal enum FieldElementKind
{
    /// <summary>No <c>Values</c>.</summary>
    None,

    /// <summary>A string; one of the field's choices when it has a fixed set.</summary>
    String,

    /// <summary>An integer in the field's range (<c>Year AnyOf</c>).</summary>
    Integer,

    /// <summary>A GUID string (library ids).</summary>
    Guid,

    /// <summary>A reference <c>{Id, Name}</c> to an item (series, people, collections).</summary>
    Reference,

    /// <summary>The name of a <c>BaseItemKind</c> (the <c>ItemType</c> field).</summary>
    ItemKind
}
