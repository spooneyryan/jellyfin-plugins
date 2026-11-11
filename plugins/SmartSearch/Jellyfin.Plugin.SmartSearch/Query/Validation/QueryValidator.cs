using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.SmartSearch.Api.Models;
using Jellyfin.Plugin.SmartSearch.Query.Fields;
using Jellyfin.Plugin.SmartSearch.Query.Migration;

namespace Jellyfin.Plugin.SmartSearch.Query.Validation;

/// <summary>
/// Checks a query: its structure and limits, each rule's field, operator, values and options,
/// admin-only and unavailable fields. The same validator runs on save, on search and on import. It reads the query
/// as the reader made it; the canonicalizer runs only on a valid one.
/// </summary>
internal sealed class QueryValidator
{
    private const RegexOptions PatternOptions = RegexOptions.NonBacktracking | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private readonly IFieldCatalog _catalog;

    /// <summary>
    /// Initializes a new instance of the <see cref="QueryValidator"/> class.
    /// </summary>
    /// <param name="catalog">The fields and sort keys.</param>
    public QueryValidator(IFieldCatalog catalog)
    {
        _catalog = catalog;
    }

    /// <summary>Compiles a <c>Matches</c> pattern the way the validator and the evaluator do.</summary>
    /// <param name="pattern">The pattern.</param>
    /// <returns>The regular expression.</returns>
    /// <exception cref="ArgumentException">The pattern doesn't compile.</exception>
    /// <exception cref="NotSupportedException">The pattern needs backtracking (back references, lookarounds).</exception>
    public static Regex CompilePattern(string pattern) => new(pattern, PatternOptions, QueryLimits.RegexTimeout);

    /// <summary>Validates a query.</summary>
    /// <param name="query">The query.</param>
    /// <param name="context">Who validates and under which settings.</param>
    /// <returns>The errors, and what the query uses.</returns>
    public QueryValidationResult Validate(SmartSearchQueryDefinition query, QueryValidationContext context)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var state = new State(context) { Newer = query.Version > QueryMigrator.CurrentVersion };
        CheckUnknown(query.Extra, string.Empty, state);
        if (query.Version != QueryMigrator.CurrentVersion)
        {
            if (query.Version > QueryMigrator.CurrentVersion)
            {
                state.Add("Version", QueryErrorCodes.UnsupportedVersion, "The query was saved by a newer version of Smart Search.");
            }
            else
            {
                state.Add("Version", QueryErrorCodes.InvalidVersion, "Version must be a whole number of 1 or more.");
            }
        }

        ValidateScope(query.Scope, state);
        if (query.Where is null)
        {
            state.Add("Where", QueryErrorCodes.Required, "The query has no rule tree.");
        }
        else if (!query.Where.IsGroup)
        {
            state.Add("Where", QueryErrorCodes.RootNotGroup, "The root of the rule tree must be a group.");
        }
        else
        {
            ValidateGroup(query.Where, "Where", 1, state);
        }

        if (state.Rules > QueryLimits.MaxRules)
        {
            state.Add("Where", QueryErrorCodes.TooManyRules, "A query has at most 50 rules.");
        }

        ValidateSort(query.Sort, state);
        if (query.Limit is { } limit && (limit < 1 || limit > QueryLimits.MaxLimit))
        {
            state.Add("Limit", QueryErrorCodes.LimitOutOfRange, "Limit must be from 1 to 10,000, or none.");
        }

        return new QueryValidationResult(state.Errors.Take(QueryLimits.MaxErrors).ToList(), state.PerUser, state.AdminOnly, state.Unavailable);
    }

    private static void ValidateScope(SmartSearchQueryScope? scope, State state)
    {
        if (scope is null)
        {
            state.Add("Scope", QueryErrorCodes.Required, "The query has no scope.");
            return;
        }

        CheckUnknown(scope.Extra, "Scope", state);

        if (scope.ItemKinds is null)
        {
            state.Add("Scope.ItemKinds", QueryErrorCodes.Required, "The scope names no item kinds.");
        }
        else if (scope.ItemKinds.Count == 0)
        {
            state.Add("Scope.ItemKinds", QueryErrorCodes.NoItemKinds, "Choose at least one item kind.");
        }
        else
        {
            for (var i = 0; i < scope.ItemKinds.Count; i++)
            {
                var kind = scope.ItemKinds[i];
                if (state.IsKindAllowed(kind))
                {
                    state.ScopeKinds.Add(kind);
                }
                else
                {
                    state.Add("Scope.ItemKinds[" + i + "]", QueryErrorCodes.ItemKindNotAllowed, "This item kind can't be searched.");
                }
            }
        }

        if (scope.LibraryIds is null)
        {
            state.Add("Scope.LibraryIds", QueryErrorCodes.Required, "LibraryIds must be a list (empty for every library).");
            return;
        }

        for (var i = 0; i < scope.LibraryIds.Count; i++)
        {
            if (scope.LibraryIds[i] == Guid.Empty)
            {
                state.Add("Scope.LibraryIds[" + i + "]", QueryErrorCodes.InvalidLibraryId, "A library id can't be empty.");
            }
        }
    }

    private void ValidateSort(List<SmartSearchQuerySort>? sort, State state)
    {
        if (sort is null)
        {
            state.Add("Sort", QueryErrorCodes.Required, "Sort must be a list (empty for by title).");
            return;
        }

        if (sort.Count > QueryLimits.MaxSortKeys)
        {
            state.Add("Sort", QueryErrorCodes.TooManySortKeys, "A query has at most 3 sort keys.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < sort.Count; i++)
        {
            var path = "Sort[" + i + "]";
            if (sort[i] is not { Key: { } key } entry || string.IsNullOrWhiteSpace(key))
            {
                state.Add(path + ".Key", QueryErrorCodes.Required, "A sort entry needs a key.");
                continue;
            }

            CheckUnknown(entry.Extra, path, state);

            if (!Enum.IsDefined(entry.Order))
            {
                state.Add(path + ".Order", QueryErrorCodes.InvalidValue, "Order is Ascending or Descending.");
            }

            var canonical = _catalog.FindSortKey(key.Trim());
            if (canonical is null)
            {
                state.Add(path + ".Key", QueryErrorCodes.UnknownSortKey, "This sort key isn't known to this version of Smart Search.");
            }
            else if (!seen.Add(canonical))
            {
                state.Add(path + ".Key", QueryErrorCodes.DuplicateSortKey, "A sort key can be used once.");
            }
        }
    }

    private void ValidateGroup(SmartSearchQueryNode group, string path, int depth, State state)
    {
        if (depth > QueryLimits.MaxDepth)
        {
            state.Add(path, QueryErrorCodes.TooDeep, "Groups can be nested at most 4 deep.");
            return;
        }

        CheckUnknown(group.Extra, path, state);
        if (group.Field is not null)
        {
            state.Add(path, QueryErrorCodes.GroupAndRule, "A node is a group (Children) or a rule (Field), not both.");
        }
        else
        {
            foreach (var property in RuleProperties(group))
            {
                state.Add(path + "." + property, QueryErrorCodes.UnexpectedProperty, "A group doesn't take " + property + ".");
            }
        }

        if (group.Match is { } match && !Enum.IsDefined(match))
        {
            state.Add(path + ".Match", QueryErrorCodes.InvalidValue, "Match is All or Any.");
        }

        var children = group.Children!;
        if (children.Count == 0 && depth > 1)
        {
            state.Add(path, QueryErrorCodes.EmptyGroup, "A group needs at least one rule.");
        }
        else if (children.Count == 0 && (group.Match == SmartSearchMatchMode.Any || group.Negate))
        {
            // An empty root matches everything in scope; "any of nothing" or "not everything" would match nothing.
            state.Add(path, QueryErrorCodes.EmptyGroup, "An empty root matches everything in scope, so it can't be Any or negated.");
        }

        if (children.Count > QueryLimits.MaxChildren)
        {
            state.Add(path, QueryErrorCodes.TooManyChildren, "A group has at most 50 children.");
        }

        for (var i = 0; i < children.Count; i++)
        {
            var childPath = path + ".Children[" + i + "]";
            var child = children[i];
            if (child is null)
            {
                state.Add(childPath, QueryErrorCodes.EmptyNode, "A node is a group or a rule.");
            }
            else if (child.IsGroup)
            {
                ValidateGroup(child, childPath, depth + 1, state);
            }
            else if (child.Field is not null)
            {
                ValidateRule(child, childPath, state);
            }
            else if (child.Op is not null || RuleProperties(child).Any())
            {
                state.Rules++;
                state.Add(childPath + ".Field", QueryErrorCodes.Required, "A rule needs a field.");
            }
            else
            {
                state.Add(childPath, QueryErrorCodes.EmptyNode, "A node is a group or a rule.");
            }
        }
    }

    private void ValidateRule(SmartSearchQueryNode rule, string path, State state)
    {
        state.Rules++;
        CheckUnknown(rule.Extra, path, state);
        if (rule.Match is not null)
        {
            state.Add(path + ".Match", QueryErrorCodes.UnexpectedProperty, "A rule doesn't take Match.");
        }

        if (rule.Negate)
        {
            state.Add(path + ".Negate", QueryErrorCodes.UnexpectedProperty, "A rule doesn't take Negate; use the negative operator.");
        }

        if (string.IsNullOrWhiteSpace(rule.Field))
        {
            state.Add(path + ".Field", QueryErrorCodes.Required, "A rule needs a field.");
            return;
        }

        var field = _catalog.Find(rule.Field.Trim());
        if (field is null)
        {
            state.Add(path + ".Field", QueryErrorCodes.UnknownField, "This field isn't known to this version of Smart Search.");
            return;
        }

        if (field.AdminOnly)
        {
            state.AdminOnly = true;
            if (!state.Context.IsAdmin)
            {
                state.Add(path + ".Field", QueryErrorCodes.AdminOnlyField, "Only administrators can use this field.");
            }
        }

        state.PerUser |= field.PerUser;
        if (!field.Available && !state.Unavailable.Contains(field.Id))
        {
            state.Unavailable.Add(field.Id);
        }

        if (state.ScopeKinds.Count > 0 && !field.Kinds.Any(state.ScopeKinds.Contains))
        {
            state.Add(path + ".Field", QueryErrorCodes.FieldNotForKinds, "This field doesn't apply to the item kinds searched.");
        }

        ValidateOptions(rule, field, path, state);
        if (rule.Op is not { } op)
        {
            state.Add(path + ".Op", QueryErrorCodes.Required, "A rule needs an operator.");
            return;
        }

        if (!field.Operators.Contains(op))
        {
            state.Add(path + ".Op", QueryErrorCodes.OperatorNotAllowed, "This field doesn't take this operator.");
            return;
        }

        if (op == SmartSearchQueryOperator.Matches && !state.Context.EnableRegex)
        {
            // Refused before the pattern is read, so a server with regex off never compiles one.
            state.Add(path + ".Op", QueryErrorCodes.RegexDisabled, "Regular expressions are turned off on this server.");
            return;
        }

        var shape = QueryOperators.ShapeOf(op);
        foreach (var property in ValueProperties(rule).Where(p => !Reads(shape, p)))
        {
            state.Add(path + "." + property, QueryErrorCodes.UnexpectedProperty, "This operator doesn't take " + property + ".");
        }

        switch (shape)
        {
            case OperatorShape.Value:
                if (rule.Value is { } value)
                {
                    ReadScalar(field, op, value, path + ".Value", state);
                }
                else
                {
                    state.Add(path + ".Value", QueryErrorCodes.Required, "This operator needs a value.");
                }

                break;
            case OperatorShape.Range:
                ValidateRange(rule, field, op, path, state);
                break;
            case OperatorShape.Relative:
                ValidateRelative(rule, path, state);
                break;
            case OperatorShape.Values:
                ValidateValues(rule.Values, field, path + ".Values", state);
                break;
        }
    }

    private static void ValidateOptions(SmartSearchQueryNode rule, FieldDescriptor field, string path, State state)
    {
        foreach (var option in Enum.GetValues<FieldOption>())
        {
            var spec = field.Options.FirstOrDefault(o => o.Option == option);
            var optionPath = path + "." + option;
            var set = option switch
            {
                FieldOption.IncludeOriginalTitle => rule.IncludeOriginalTitle is not null,
                FieldOption.Role => !string.IsNullOrEmpty(rule.Role),
                _ => !string.IsNullOrEmpty(rule.Provider)
            };
            if (spec is null)
            {
                if (set || (option == FieldOption.Role && rule.Role is not null) || (option == FieldOption.Provider && rule.Provider is not null))
                {
                    state.Add(optionPath, QueryErrorCodes.OptionNotAllowed, "This field doesn't take " + option + ".");
                }

                continue;
            }

            if (!set)
            {
                if (spec.Required)
                {
                    state.Add(optionPath, QueryErrorCodes.MissingOption, "This field needs " + option + ".");
                }

                continue;
            }

            var text = option == FieldOption.Role ? rule.Role : option == FieldOption.Provider ? rule.Provider : null;
            if (text is not null && spec.Choices is { } choices && !choices.Contains(text.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                state.Add(optionPath, QueryErrorCodes.InvalidOption, "This is not one of the values " + option + " takes.");
            }
        }
    }

    private static void ValidateRange(SmartSearchQueryNode rule, FieldDescriptor field, SmartSearchQueryOperator op, string path, State state)
    {
        if (rule.From is not { } from)
        {
            state.Add(path + ".From", QueryErrorCodes.Required, "Between needs From.");
        }

        if (rule.To is not { } to)
        {
            state.Add(path + ".To", QueryErrorCodes.Required, "Between needs To.");
        }

        if (rule.From is null || rule.To is null)
        {
            return;
        }

        var low = ReadScalar(field, op, rule.From.Value, path + ".From", state);
        var high = ReadScalar(field, op, rule.To.Value, path + ".To", state);
        if (low is not { } a || high is not { } b)
        {
            return;
        }

        if (a > b)
        {
            state.Add(path, QueryErrorCodes.InvalidRange, "From must not be after To.");
        }
        else if (field.MaxSpan is { } span && b - a > span)
        {
            state.Add(path, QueryErrorCodes.InvalidRange, "The range is too wide (at most " + span + ").");
        }
    }

    private static void ValidateRelative(SmartSearchQueryNode rule, string path, State state)
    {
        if (rule.Unit is not { } unit || !Enum.IsDefined(unit))
        {
            state.Add(path + ".Unit", QueryErrorCodes.Required, "This operator needs a unit: Days, Weeks, Months or Years.");
            unit = SmartSearchRelativeUnit.Days;
        }

        if (rule.Amount is not { } amount)
        {
            state.Add(path + ".Amount", QueryErrorCodes.Required, "This operator needs an amount.");
        }
        else if (amount < 1 || amount > QueryLimits.MaxRelativeAmount(unit))
        {
            state.Add(path + ".Amount", QueryErrorCodes.OutOfRange, "The amount must be from 1 to 3650 days, or the same in weeks, months or years.");
        }
    }

    private static void ValidateValues(List<JsonElement>? values, FieldDescriptor field, string path, State state)
    {
        if (values is null || values.Count == 0)
        {
            state.Add(path, QueryErrorCodes.Required, "This operator needs at least one value.");
            return;
        }

        if (values.Count > QueryLimits.MaxValues)
        {
            state.Add(path, QueryErrorCodes.TooManyValues, "A rule has at most 100 values.");
        }

        for (var i = 0; i < values.Count; i++)
        {
            ValidateElement(values[i], field, path + "[" + i + "]", state);
        }
    }

    private static void ValidateElement(JsonElement value, FieldDescriptor field, string path, State state)
    {
        switch (field.ElementKind)
        {
            case FieldElementKind.String:
                ReadText(value, field, false, path, state);
                break;
            case FieldElementKind.Integer:
                ReadNumber(value, field, true, path, state);
                break;
            case FieldElementKind.Guid:
                if (value.ValueKind != JsonValueKind.String || !Guid.TryParse(value.GetString(), out var id) || id == Guid.Empty)
                {
                    state.Add(path, QueryErrorCodes.InvalidValue, "This must be an id.");
                }

                break;
            case FieldElementKind.Reference:
                ValidateReference(value, path, state);
                break;
            case FieldElementKind.ItemKind:
                if (ItemKindOf(value) is not { } kind)
                {
                    state.Add(path, QueryErrorCodes.InvalidValue, "This must be an item kind.");
                }
                else if (!state.IsKindAllowed(kind))
                {
                    state.Add(path, QueryErrorCodes.ItemKindNotAllowed, "This item kind can't be searched.");
                }

                break;
            default:
                state.Add(path, QueryErrorCodes.InvalidValue, "This field takes no list of values.");
                break;
        }
    }

    /// <summary>Reads an item kind by its name, ignoring case; numbers and other text are no kind.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The kind, or <c>null</c>.</returns>
    internal static BaseItemKind? ItemKindOf(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString();
        var name = Enum.GetNames<BaseItemKind>().FirstOrDefault(n => string.Equals(n, text, StringComparison.OrdinalIgnoreCase));
        return name is null ? null : Enum.Parse<BaseItemKind>(name);
    }

    private static void ValidateReference(JsonElement value, string path, State state)
    {
        var read = QueryReference.Read(value);
        if (!read.Valid)
        {
            state.Add(path + read.Path, QueryErrorCodes.InvalidValue, read.Message);
        }
    }

    // Properties the model doesn't have (they land in Extra): an error in a query of this version, expected in a newer
    // one, which is unsupported as a whole.
    private static void CheckUnknown(Dictionary<string, JsonElement>? extra, string path, State state)
    {
        if (extra is null || state.Newer)
        {
            return;
        }

        foreach (var name in extra.Keys)
        {
            state.Add(path.Length == 0 ? name : path + "." + name, QueryErrorCodes.UnknownProperty, "This property is no part of a Smart Search query.");
        }
    }

    // Reads one scalar (Value, From, To) as the field's type and reports what is wrong with it. Numbers and dates come
    // back comparable (dates as UTC ticks) for the Between checks; text and bools come back null.
    private static double? ReadScalar(FieldDescriptor field, SmartSearchQueryOperator op, JsonElement value, string path, State state)
    {
        switch (field.Type)
        {
            case FieldValueType.Number:
                return ReadNumber(value, field, field.Integer, path, state);
            case FieldValueType.Date:
                if (value.ValueKind != JsonValueKind.String || !QueryDates.TryParse(value.GetString()!, out var date))
                {
                    state.Add(path, QueryErrorCodes.InvalidValue, "This must be a date (yyyy-MM-dd, or a time with Z or an offset).");
                    return null;
                }

                if (date.Utc.Year < QueryLimits.MinYear || date.Utc.Year > QueryLimits.MaxYear)
                {
                    state.Add(path, QueryErrorCodes.OutOfRange, "Dates must be in the years 1800 to 2100.");
                    return null;
                }

                return date.Utc.Ticks;
            case FieldValueType.Bool:
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    state.Add(path, QueryErrorCodes.InvalidValue, "This must be true or false.");
                }

                return null;
            default:
                ReadText(value, field, op == SmartSearchQueryOperator.Matches, path, state);
                return null;
        }
    }

    private static double? ReadNumber(JsonElement value, FieldDescriptor field, bool integer, string path, State state)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number))
        {
            state.Add(path, QueryErrorCodes.InvalidValue, "This must be a number.");
            return null;
        }

        if (integer && number != Math.Floor(number))
        {
            state.Add(path, QueryErrorCodes.InvalidValue, "This must be a whole number.");
            return null;
        }

        if ((field.Min is { } min && number < min) || (field.Max is { } max && number > max))
        {
            state.Add(path, QueryErrorCodes.OutOfRange, "This must be from " + field.Min + " to " + field.Max + ".");
            return null;
        }

        return number;
    }

    private static void ReadText(JsonElement value, FieldDescriptor field, bool pattern, string path, State state)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            state.Add(path, QueryErrorCodes.InvalidValue, "This must be text.");
            return;
        }

        var text = value.GetString()!;
        var trimmed = pattern ? text : text.Trim();
        if (trimmed.Length == 0)
        {
            state.Add(path, QueryErrorCodes.EmptyText, "This can't be empty.");
            return;
        }

        if (trimmed.Length > QueryLimits.MaxTextLength)
        {
            state.Add(path, QueryErrorCodes.TextTooLong, "This is over 200 characters.");
            return;
        }

        if (pattern)
        {
            try
            {
                _ = CompilePattern(text);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                state.Add(path, QueryErrorCodes.InvalidRegex, "This regular expression doesn't compile (back references and lookarounds aren't supported).");
            }

            return;
        }

        if (field.Choices is { } choices && !choices.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
        {
            state.Add(path, QueryErrorCodes.InvalidValue, "This is not one of the field's values.");
        }
    }

    private static IEnumerable<string> RuleProperties(SmartSearchQueryNode node)
    {
        if (node.Op is not null)
        {
            yield return nameof(node.Op);
        }

        foreach (var property in ValueProperties(node))
        {
            yield return property;
        }

        if (node.Role is not null)
        {
            yield return nameof(node.Role);
        }

        if (node.Provider is not null)
        {
            yield return nameof(node.Provider);
        }

        if (node.IncludeOriginalTitle is not null)
        {
            yield return nameof(node.IncludeOriginalTitle);
        }
    }

    private static IEnumerable<string> ValueProperties(SmartSearchQueryNode node)
    {
        if (node.Value is not null)
        {
            yield return nameof(node.Value);
        }

        if (node.Values is not null)
        {
            yield return nameof(node.Values);
        }

        if (node.From is not null)
        {
            yield return nameof(node.From);
        }

        if (node.To is not null)
        {
            yield return nameof(node.To);
        }

        if (node.Amount is not null)
        {
            yield return nameof(node.Amount);
        }

        if (node.Unit is not null)
        {
            yield return nameof(node.Unit);
        }
    }

    private static bool Reads(OperatorShape shape, string property) => shape switch
    {
        OperatorShape.Value => property == nameof(SmartSearchQueryNode.Value),
        OperatorShape.Range => property is nameof(SmartSearchQueryNode.From) or nameof(SmartSearchQueryNode.To),
        OperatorShape.Relative => property is nameof(SmartSearchQueryNode.Amount) or nameof(SmartSearchQueryNode.Unit),
        OperatorShape.Values => property == nameof(SmartSearchQueryNode.Values),
        _ => false
    };

    private sealed class State(QueryValidationContext context)
    {
        public QueryValidationContext Context { get; } = context;

        public List<QueryValidationError> Errors { get; } = [];

        public HashSet<BaseItemKind> ScopeKinds { get; } = [];

        public List<string> Unavailable { get; } = [];

        public int Rules { get; set; }

        public bool PerUser { get; set; }

        public bool AdminOnly { get; set; }

        public bool Newer { get; init; }

        public bool IsKindAllowed(BaseItemKind kind) => QueryLimits.ItemKinds.Contains(kind) && Context.AllowedItemKinds.Contains(kind);

        public void Add(string path, string code, string message) => Errors.Add(new QueryValidationError(path, code, message));
    }
}
