using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.SmartSearch.Api.Models;
using Jellyfin.Plugin.SmartSearch.Query;
using Jellyfin.Plugin.SmartSearch.Query.Validation;

namespace Jellyfin.Plugin.SmartSearch.Storage;

/// <summary>
/// Reads stored queries (<see cref="SavedQueryFacts"/>), each record once: records are immutable, so the facts of one
/// are kept with it for as long as it lives.
/// </summary>
internal sealed class SavedQueryReader
{
    private readonly QueryValidator _validator;
    private readonly QueryCanonicalizer _canonicalizer;
    private readonly ConditionalWeakTable<SavedQueryRecord, SavedQueryFacts> _facts = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="SavedQueryReader"/> class.
    /// </summary>
    /// <param name="validator">The validator.</param>
    /// <param name="canonicalizer">The canonicalizer.</param>
    public SavedQueryReader(QueryValidator validator, QueryCanonicalizer canonicalizer)
    {
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(canonicalizer);
        _validator = validator;
        _canonicalizer = canonicalizer;
    }

    /// <summary>Gets the facts of a record.</summary>
    /// <param name="record">The record.</param>
    /// <returns>Its facts.</returns>
    public SavedQueryFacts Read(SavedQueryRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return _facts.GetValue(record, r => Read(r.Query));
    }

    /// <summary>Gets the facts of a query's JSON.</summary>
    /// <param name="query">The JSON.</param>
    /// <returns>Its facts.</returns>
    public SavedQueryFacts Read(JsonElement query)
    {
        if (query.ValueKind != JsonValueKind.Object)
        {
            return new SavedQueryFacts(null, true, false, [new QueryValidationError(string.Empty, QueryErrorCodes.NotAnObject, "The query is not a JSON object.")], false, false, null);
        }

        var read = QueryJson.Read(Encoding.UTF8.GetBytes(query.GetRawText()));
        return read.Definition is null
            ? new SavedQueryFacts(null, read.Supported, false, read.Errors, false, false, null)
            : Read(read.Definition);
    }

    /// <summary>Gets the facts of a query this build read.</summary>
    /// <param name="definition">The query.</param>
    /// <returns>Its facts.</returns>
    public SavedQueryFacts Read(SmartSearchQueryDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var validation = _validator.Validate(definition, QueryValidationContext.Admin);
        return new SavedQueryFacts(
            definition,
            validation.Supported,
            validation.Valid,
            validation.Errors.ToList(),
            validation.UsesPerUserFields,
            validation.UsesAdminOnlyFields,
            validation.Valid ? _canonicalizer.Canonicalize(definition).Hash : null);
    }
}
