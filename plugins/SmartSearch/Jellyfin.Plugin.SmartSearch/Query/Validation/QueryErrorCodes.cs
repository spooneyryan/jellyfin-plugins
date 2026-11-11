using System.Collections.Frozen;

namespace Jellyfin.Plugin.SmartSearch.Query.Validation;

/// <summary>
/// The codes of <see cref="QueryValidationError"/>. The client keys its messages on them.
/// </summary>
internal static class QueryErrorCodes
{
    /// <summary>The serialized query is over 64 KB.</summary>
    public const string TooLarge = "TooLarge";

    /// <summary>The text isn't JSON, or a property has the wrong JSON type.</summary>
    public const string InvalidJson = "InvalidJson";

    /// <summary>The JSON isn't an object.</summary>
    public const string NotAnObject = "NotAnObject";

    /// <summary><c>Version</c> isn't a whole number of 1 or more.</summary>
    public const string InvalidVersion = "InvalidVersion";

    /// <summary><c>Version</c> is newer than this build reads (unsupported).</summary>
    public const string UnsupportedVersion = "UnsupportedVersion";

    /// <summary>An <c>Op</c>, <c>Match</c> or <c>Unit</c> this build doesn't know (unsupported).</summary>
    public const string UnknownOperator = "UnknownOperator";

    /// <summary>A field this build doesn't know (unsupported).</summary>
    public const string UnknownField = "UnknownField";

    /// <summary>A sort key this build doesn't know (unsupported, like a field).</summary>
    public const string UnknownSortKey = "UnknownSortKey";

    /// <summary>A required property is missing or null.</summary>
    public const string Required = "Required";

    /// <summary>The root (<c>Where</c>) is not a group.</summary>
    public const string RootNotGroup = "RootNotGroup";

    /// <summary>Groups are nested deeper than 4.</summary>
    public const string TooDeep = "TooDeep";

    /// <summary>More than 50 rules.</summary>
    public const string TooManyRules = "TooManyRules";

    /// <summary>A group has more than 50 children.</summary>
    public const string TooManyChildren = "TooManyChildren";

    /// <summary>A rule has more than 100 values.</summary>
    public const string TooManyValues = "TooManyValues";

    /// <summary>A group below the root has no children.</summary>
    public const string EmptyGroup = "EmptyGroup";

    /// <summary>A node is neither a group nor a rule.</summary>
    public const string EmptyNode = "EmptyNode";

    /// <summary>A node is both a group and a rule.</summary>
    public const string GroupAndRule = "GroupAndRule";

    /// <summary>A node sets a property its kind or operator doesn't read.</summary>
    public const string UnexpectedProperty = "UnexpectedProperty";

    /// <summary>A property that is no part of this version's query (a newer query says so with a newer <c>Version</c>).</summary>
    public const string UnknownProperty = "UnknownProperty";

    /// <summary>Only administrators may use the field.</summary>
    public const string AdminOnlyField = "AdminOnlyField";

    /// <summary>The field applies to none of the query's item kinds.</summary>
    public const string FieldNotForKinds = "FieldNotForKinds";

    /// <summary>The field doesn't take the operator.</summary>
    public const string OperatorNotAllowed = "OperatorNotAllowed";

    /// <summary>A value has the wrong type, or isn't one of the field's choices.</summary>
    public const string InvalidValue = "InvalidValue";

    /// <summary>A text value is empty after trimming.</summary>
    public const string EmptyText = "EmptyText";

    /// <summary>A text value or pattern is over 200 characters.</summary>
    public const string TextTooLong = "TextTooLong";

    /// <summary>A number, year, date or amount is outside its range.</summary>
    public const string OutOfRange = "OutOfRange";

    /// <summary><c>From</c> is after <c>To</c>, or the span is too wide.</summary>
    public const string InvalidRange = "InvalidRange";

    /// <summary>A <c>Matches</c> pattern doesn't compile.</summary>
    public const string InvalidRegex = "InvalidRegex";

    /// <summary><c>Matches</c> is turned off (setting <c>EnableRegex</c>).</summary>
    public const string RegexDisabled = "RegexDisabled";

    /// <summary>The field doesn't take the option.</summary>
    public const string OptionNotAllowed = "OptionNotAllowed";

    /// <summary>The field needs the option.</summary>
    public const string MissingOption = "MissingOption";

    /// <summary>The option's value isn't one of its choices.</summary>
    public const string InvalidOption = "InvalidOption";

    /// <summary>The scope names no item kind.</summary>
    public const string NoItemKinds = "NoItemKinds";

    /// <summary>The scope names an item kind that can't be searched.</summary>
    public const string ItemKindNotAllowed = "ItemKindNotAllowed";

    /// <summary>The scope names the empty library id.</summary>
    public const string InvalidLibraryId = "InvalidLibraryId";

    /// <summary>More than 3 sort keys.</summary>
    public const string TooManySortKeys = "TooManySortKeys";

    /// <summary>A sort key repeats.</summary>
    public const string DuplicateSortKey = "DuplicateSortKey";

    /// <summary><c>Limit</c> is outside 1 to 10,000.</summary>
    public const string LimitOutOfRange = "LimitOutOfRange";

    /// <summary>Gets the codes that make a query unsupported rather than invalid: it is kept, but not run or edited.</summary>
    public static FrozenSet<string> Unsupported { get; } = new[] { UnsupportedVersion, UnknownOperator, UnknownField, UnknownSortKey }.ToFrozenSet();
}
