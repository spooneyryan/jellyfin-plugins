using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.SmartSearch.Api;
using Jellyfin.Plugin.SmartSearch.Api.Models;
using Jellyfin.Plugin.SmartSearch.Query;
using Jellyfin.Plugin.SmartSearch.Query.Execution;
using Jellyfin.Plugin.SmartSearch.Query.Planning;
using Jellyfin.Plugin.SmartSearch.Query.Validation;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.SmartSearch.Services;

/// <summary>
/// <c>/Search</c> and <c>/Validate</c>: the query read strictly (by the body's reader), validated for the caller
/// (administrator-only fields, the allowed item kinds), then searched or planned by the engine as the caller. The
/// engine scopes every result to the caller (their libraries, parental limit, tags and <c>IsVisible</c>), collapses
/// merged versions per version set and presentation key, and refuses rather than cuts short; this class maps its
/// answer to the DTOs.
/// </summary>
internal sealed class SmartSearchService : ISmartSearchService
{
    /// <summary>The <c>code</c> of a query with errors (400), with <c>errors</c>.</summary>
    public const string InvalidQueryCode = "InvalidQuery";

    /// <summary>The <c>code</c> of a query that uses an administrator-only field, sent by someone else (403).</summary>
    public const string AdminOnlyFieldCode = QueryErrorCodes.AdminOnlyField;

    /// <summary>The <c>code</c> of a query over 64 KB (413).</summary>
    public const string TooLargeCode = QueryErrorCodes.TooLarge;

    /// <summary>The <c>code</c> of <c>ForceResidual</c> sent by someone who isn't an administrator (403).</summary>
    public const string AdminOnlyOptionCode = "AdminOnlyOption";

    /// <summary>The warning of a query that names a library the user can't open, or that no longer exists.</summary>
    public const string LibrariesUnavailableWarning = "LibrariesUnavailable";

    private readonly ISmartSearchEngine _engine;
    private readonly QueryValidator _validator;
    private readonly LibraryAccess _access;
    private readonly ILibraryManager _library;
    private readonly Func<IReadOnlyList<BaseItemKind>> _allowedKinds;
    private readonly Func<bool> _enabled;

    /// <summary>
    /// Initializes a new instance of the <see cref="SmartSearchService"/> class.
    /// </summary>
    /// <param name="engine">The engine.</param>
    /// <param name="validator">The validator.</param>
    /// <param name="access">The kit's library access.</param>
    /// <param name="library">The library manager (whether a scan runs).</param>
    /// <param name="allowedKinds">The kinds a query may name (the setting), read on each request.</param>
    /// <param name="enabled">The setting <c>EnableSearchPage</c>, read on each request.</param>
    public SmartSearchService(ISmartSearchEngine engine, QueryValidator validator, LibraryAccess access, ILibraryManager library, Func<IReadOnlyList<BaseItemKind>> allowedKinds, Func<bool> enabled)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(allowedKinds);
        ArgumentNullException.ThrowIfNull(enabled);
        _enabled = enabled;
        _engine = engine;
        _validator = validator;
        _access = access;
        _library = library;
        _allowedKinds = allowedKinds;
    }

    /// <inheritdoc />
    public async Task<SmartSearchResultDto> SearchAsync(User user, bool isAdmin, SmartSearchRequestDto request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(request);
        SearchPageGate.Ensure(_enabled());
        if (request.ForceResidual && !isAdmin)
        {
            throw new PluginException(PluginErrorKind.Forbidden, "Only administrators can force the Residual path.") { Code = AdminOnlyOptionCode };
        }

        var read = request.Read ?? throw PluginException.Invalid("The request has no query.");
        if (read.Definition is null)
        {
            throw QueryErrors(read.Errors);
        }

        var query = read.Definition;
        var validation = _validator.Validate(query, Context(isAdmin));
        if (!validation.Valid)
        {
            throw QueryErrors(validation.Errors);
        }

        if (validation.UnavailableFields.Count > 0)
        {
            throw SmartSearchRefusedException.FieldUnavailable(validation.UnavailableFields);
        }

        var page = await _engine.SearchAsync(query, user, new PageRequest(request.StartIndex, request.Limit, request.ResultToken, request.IncludeTotal, request.ForceResidual), cancellationToken).ConfigureAwait(false);
        var warnings = page.Warnings.ToList();
        if (UnavailableLibraries(query, user) > 0)
        {
            warnings.Add(LibrariesUnavailableWarning);
        }

        return new SmartSearchResultDto
        {
            Items = page.Items,
            TotalRecordCount = page.TotalRecordCount,
            StartIndex = page.StartIndex,
            ResultToken = page.ResultToken,
            HasMore = page.HasMore,
            ScanInProgress = _library.IsScanRunning,
            Warnings = warnings,
            ElapsedMs = (long)page.Elapsed.TotalMilliseconds
        };
    }

    /// <inheritdoc />
    public async Task<SmartSearchValidationDto> ValidateAsync(User user, bool isAdmin, SmartSearchValidateRequestDto request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(request);
        SearchPageGate.Ensure(_enabled());
        var read = request.Read ?? throw PluginException.Invalid("The request has no query.");
        if (read.Definition is null)
        {
            if (read.Errors.Any(e => e.Code == QueryErrorCodes.TooLarge))
            {
                throw QueryErrors(read.Errors);
            }

            return new SmartSearchValidationDto { Valid = false, Supported = read.Supported, Errors = Dtos(read.Errors), Paths = isAdmin ? [] : null };
        }

        var validation = _validator.Validate(read.Definition, Context(isAdmin));
        var result = new SmartSearchValidationDto
        {
            Valid = validation.Valid,
            Supported = validation.Supported,
            Errors = Dtos(validation.Errors),
            UsesPerUserFields = validation.UsesPerUserFields,
            UsesAdminOnlyFields = validation.UsesAdminOnlyFields,
            UnavailableFields = validation.UnavailableFields,
            Paths = isAdmin ? [] : null
        };
        if (validation.Runnable && isAdmin)
        {
            // The plan's path is for administrators only: what a plan does depends on what the server holds, beyond what
            // the caller can see. Planning reads the database (a Library rule's items), so it takes a slot, the budget and
            // the timeout as a search does.
            var plan = await _engine.ExplainAsync(read.Definition, user, PlanPurpose.Search, cancellationToken).ConfigureAwait(false);
            result.Paths = [plan.Path.ToString()];
        }

        return result;
    }

    internal static List<SmartSearchValidationErrorDto> Dtos(IEnumerable<QueryValidationError> errors)
        => errors.Select(e => new SmartSearchValidationErrorDto { Path = e.Path, Code = e.Code, Message = e.Message }).ToList();

    // A query the reader or the validator refused: 413 over 64 KB, 403 for an administrator-only field sent by anyone else
    // (whatever else is wrong with it), else 400; each with every error and its path inside the query.
    internal static PluginException QueryErrors(IReadOnlyList<QueryValidationError> errors)
    {
        var dtos = Dtos(errors);
        var extensions = new Dictionary<string, object?> { ["errors"] = ProblemError.Of(dtos) };
        if (errors.Any(e => e.Code == QueryErrorCodes.TooLarge))
        {
            return new PluginException(PluginErrorKind.PayloadTooLarge, "The query is over 64 KB.") { Code = TooLargeCode, Extensions = extensions };
        }

        if (errors.Any(e => e.Code == QueryErrorCodes.AdminOnlyField))
        {
            return new PluginException(PluginErrorKind.Forbidden, "Only administrators can use this field.") { Code = AdminOnlyFieldCode, Extensions = extensions };
        }

        var message = errors.Count == 1 ? errors[0].Message : "The query has " + errors.Count + " problems.";
        return new PluginException(PluginErrorKind.Invalid, message) { Code = InvalidQueryCode, Extensions = extensions };
    }

    private QueryValidationContext Context(bool isAdmin) => new(isAdmin, EnableRegex: false, _allowedKinds());

    // How many of the libraries the query names resolve to none the user can open: hidden, removed or never one. One
    // count for all three, so the answer never tells a hidden library from a missing one.
    private int UnavailableLibraries(SmartSearchQueryDefinition query, User user)
        => (query.Scope.LibraryIds ?? []).Distinct().Count(id => _access.ResolveLibraries([id], user).Count == 0);
}
