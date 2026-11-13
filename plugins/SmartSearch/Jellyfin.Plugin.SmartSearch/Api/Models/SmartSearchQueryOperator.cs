namespace Jellyfin.Plugin.SmartSearch.Api.Models;

/// <summary>
/// A rule's operator. Which ones a field takes is the field's (the registry's
/// <c>Operators</c>); what each one reads from the rule is fixed (<see cref="Query.QueryOperators"/>).
/// </summary>
public enum SmartSearchQueryOperator
{
    /// <summary>Text: contains the value.</summary>
    Contains,

    /// <summary>Text: doesn't contain the value (NOT <see cref="Contains"/>).</summary>
    NotContains,

    /// <summary>Text or number: equals the value.</summary>
    Eq,

    /// <summary>Text or number: doesn't equal the value (NOT <see cref="Eq"/>).</summary>
    Ne,

    /// <summary>Text: starts with the value.</summary>
    StartsWith,

    /// <summary>Text: ends with the value.</summary>
    EndsWith,

    /// <summary>Text: matches the regular expression in the value (setting <c>EnableRegex</c>).</summary>
    Matches,

    /// <summary>Has no value.</summary>
    IsEmpty,

    /// <summary>Has a value (NOT <see cref="IsEmpty"/>).</summary>
    IsNotEmpty,

    /// <summary>Number: below the value.</summary>
    Lt,

    /// <summary>Number (or rating): at most the value.</summary>
    Le,

    /// <summary>Number: above the value.</summary>
    Gt,

    /// <summary>Number (or rating): at least the value.</summary>
    Ge,

    /// <summary>Number or date: from <c>From</c> to <c>To</c>, both included.</summary>
    Between,

    /// <summary>Date: before the value.</summary>
    Before,

    /// <summary>Date: after the value.</summary>
    After,

    /// <summary>Date: within the last <c>Amount</c> <c>Unit</c>s.</summary>
    InLast,

    /// <summary>Date: not within the last <c>Amount</c> <c>Unit</c>s (NOT <see cref="InLast"/>, so empty dates match).</summary>
    NotInLast,

    /// <summary>List or choice: has at least one of the values.</summary>
    AnyOf,

    /// <summary>List: has every one of the values.</summary>
    AllOf,

    /// <summary>List or choice: has none of the values (NOT <see cref="AnyOf"/>).</summary>
    NoneOf,

    /// <summary>Bool: is the value.</summary>
    Is,

    /// <summary>
    /// People: lacks at least one of the values (NOT <see cref="AllOf"/>). Added last, so the other members keep their
    /// numbers.
    /// </summary>
    NotAllOf
}
