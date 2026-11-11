namespace Jellyfin.Plugin.SmartSearch.Query;

/// <summary>
/// The properties of a rule an operator reads; a rule that sets any other value property is invalid.
/// </summary>
internal enum OperatorShape
{
    /// <summary>Nothing (<c>IsEmpty</c>, <c>IsNotEmpty</c>).</summary>
    None,

    /// <summary><c>Value</c>.</summary>
    Value,

    /// <summary><c>From</c> and <c>To</c> (<c>Between</c>).</summary>
    Range,

    /// <summary><c>Amount</c> and <c>Unit</c> (<c>InLast</c>, <c>NotInLast</c>).</summary>
    Relative,

    /// <summary><c>Values</c> (<c>AnyOf</c>, <c>AllOf</c>, <c>NoneOf</c>).</summary>
    Values
}
