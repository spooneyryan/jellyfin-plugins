namespace Jellyfin.Plugin.SmartSearch.Query.Fields;

/// <summary>
/// A field's value type, which decides what its operators read from a rule.
/// </summary>
internal enum FieldValueType
{
    /// <summary>Text: <c>Value</c> is a string.</summary>
    Text,

    /// <summary>A number: <c>Value</c>, <c>From</c> and <c>To</c> are numbers (and <c>Values</c> integers for <c>Year AnyOf</c>).</summary>
    Number,

    /// <summary>A date: <c>Value</c>, <c>From</c> and <c>To</c> are dates; <c>Amount</c> and <c>Unit</c> for relative rules.</summary>
    Date,

    /// <summary>A list of strings an item carries (genres, tags, studios): <c>Values</c>.</summary>
    List,

    /// <summary>One value an item has, picked from a set: <c>Values</c> (and <c>Value</c> for a rating's <c>Le</c>/<c>Ge</c>).</summary>
    Choice,

    /// <summary>True or false: <c>Value</c> is a bool.</summary>
    Bool
}
