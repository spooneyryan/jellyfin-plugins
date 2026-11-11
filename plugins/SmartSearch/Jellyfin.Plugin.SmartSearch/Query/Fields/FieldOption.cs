namespace Jellyfin.Plugin.SmartSearch.Query.Fields;

/// <summary>
/// The per-rule options a field may take: each is a property of the rule node.
/// </summary>
internal enum FieldOption
{
    /// <summary><c>IncludeOriginalTitle</c> (bool): a <c>Title</c> rule also matches the original title.</summary>
    IncludeOriginalTitle,

    /// <summary><c>Role</c> (string): the person's role of a <c>People</c> rule.</summary>
    Role,

    /// <summary><c>Provider</c> (string): the metadata provider of an identity rule.</summary>
    Provider
}
