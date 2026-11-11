using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.SmartSearch.Api.Models;
using Jellyfin.Plugin.SmartSearch.Query;
using Jellyfin.Plugin.SmartSearch.Query.Validation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jellyfin.Plugin.SmartSearch.Api;

/// <summary>
/// Reads the body of <c>/Search</c> and <c>/Validate</c>: at most <see cref="SmartSearchRequestLimits.MaxBodyBytes"/>
/// (413 past it), one JSON object whose names are matched ignoring case, each given once, and none the request doesn't
/// have (400 <c>InvalidRequest</c> with <c>errors</c>, whose items are camelCase like the problem's members). The query inside goes through <c>QueryJson.Read</c>,
/// as a saved query does; what it made of it rides on the request (<c>Read</c>) for the service, which answers its errors.
/// </summary>
/// <remarks>
/// Not MVC's JSON input formatter: Jellyfin's serializer options ignore unknown properties, take the last of a repeated
/// name and bind numbers MVC's way, so a typo such as <c>"Negat": true</c> would silently change what a query means.
/// </remarks>
internal sealed class SmartSearchRequestBinder : IModelBinder
{
    /// <summary>The <c>code</c> of a body that isn't a request.</summary>
    public const string InvalidRequestCode = "InvalidRequest";

    /// <summary>The 413 message.</summary>
    public const string TooLargeMessage = "The request is too large.";

    /// <inheritdoc />
    public async Task BindModelAsync(ModelBindingContext bindingContext)
    {
        ArgumentNullException.ThrowIfNull(bindingContext);
        var http = bindingContext.HttpContext;
        var type = bindingContext.ModelType;
        var maxBytes = type == typeof(SmartSearchSavedQueryRequestDto) || type == typeof(SmartSearchAutoCollectionPreviewRequestDto) || type == typeof(SmartSearchAutoCollectionRequestDto) ? SmartSearchRequestLimits.MaxSaveBodyBytes : SmartSearchRequestLimits.MaxBodyBytes;
        var body = await ReadBodyAsync(http.Request.Body, maxBytes, http.RequestAborted).ConfigureAwait(false);
        object model = type == typeof(SmartSearchValidateRequestDto) ? ReadValidate(body)
            : type == typeof(SmartSearchSavedQueryRequestDto) ? ReadSave(body, HttpMethods.IsPut(http.Request.Method))
            : type == typeof(SmartSearchSavedQueryRunDto) ? ReadRun(body)
            : type == typeof(SmartSearchSavedQueryDuplicateDto) ? ReadDuplicate(body)
            : type == typeof(SmartSearchSavedQueryOwnerDto) ? ReadOwner(body)
            : type == typeof(SmartSearchApplyPolicyDto) ? ReadApplyPolicy(body)
            : type == typeof(SmartSearchAutoCollectionCheckNameRequestDto) ? ReadCheckName(body)
            : type == typeof(SmartSearchAutoCollectionPreviewRequestDto) ? ReadPreview(body)
            : type == typeof(SmartSearchAutoCollectionRequestDto) ? ReadAutoCollection(body, HttpMethods.IsPut(http.Request.Method))
            : type == typeof(SmartSearchAutoCollectionRecreateRequestDto) ? ReadRecreate(body)
            : type == typeof(SmartSearchAutoCollectionGuardRequestDto) ? ReadGuard(body)
            : ReadSearch(body);
        bindingContext.Result = ModelBindingResult.Success(model);
    }

    /// <summary>Reads a search request.</summary>
    /// <param name="body">The body, UTF-8.</param>
    /// <returns>The request.</returns>
    /// <exception cref="PluginException">400 <c>InvalidRequest</c>, or 413.</exception>
    public static SmartSearchRequestDto ReadSearch(ReadOnlySpan<byte> body)
    {
        var request = new SmartSearchRequestDto();
        var errors = new List<SmartSearchValidationErrorDto>();
        using var document = Parse(body);
        foreach (var property in Properties(document.RootElement, errors))
        {
            var value = property.Value;
            switch (property.Name.ToUpperInvariant())
            {
                case "QUERY":
                    request.Read = ReadQuery(value);
                    break;
                case "STARTINDEX":
                    request.StartIndex = Int(value, nameof(SmartSearchRequestDto.StartIndex), errors) ?? request.StartIndex;
                    break;
                case "LIMIT":
                    request.Limit = Int(value, nameof(SmartSearchRequestDto.Limit), errors) ?? request.Limit;
                    break;
                case "RESULTTOKEN":
                    if (value.ValueKind is JsonValueKind.String or JsonValueKind.Null)
                    {
                        request.ResultToken = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                    }
                    else
                    {
                        errors.Add(Error(nameof(SmartSearchRequestDto.ResultToken), QueryErrorCodes.InvalidValue, "ResultToken must be a string or null."));
                    }

                    break;
                case "INCLUDETOTAL":
                    if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        request.IncludeTotal = value.GetBoolean();
                    }
                    else
                    {
                        errors.Add(Error(nameof(SmartSearchRequestDto.IncludeTotal), QueryErrorCodes.InvalidValue, "IncludeTotal must be true or false."));
                    }

                    break;
                case "FORCERESIDUAL":
                    request.ForceResidual = Bool(value, nameof(SmartSearchRequestDto.ForceResidual), errors) ?? request.ForceResidual;
                    break;
                default:
                    errors.Add(Error(property.Name, QueryErrorCodes.UnknownProperty, "A search request has no such property."));
                    break;
            }
        }

        if (request.StartIndex < 0)
        {
            errors.Add(Error(nameof(SmartSearchRequestDto.StartIndex), QueryErrorCodes.OutOfRange, "StartIndex must be 0 or more."));
        }

        if (request.Limit < 1 || request.Limit > SmartSearchRequestLimits.MaxLimit)
        {
            errors.Add(Error(nameof(SmartSearchRequestDto.Limit), QueryErrorCodes.OutOfRange, "Limit must be from 1 to 200."));
        }

        Throw(errors);
        request.Read ??= MissingQuery();
        request.Query = request.Read.Definition;
        return request;
    }

    /// <summary>Reads a validate request.</summary>
    /// <param name="body">The body, UTF-8.</param>
    /// <returns>The request.</returns>
    /// <exception cref="PluginException">400 <c>InvalidRequest</c>, or 413.</exception>
    public static SmartSearchValidateRequestDto ReadValidate(ReadOnlySpan<byte> body)
    {
        var request = new SmartSearchValidateRequestDto();
        var errors = new List<SmartSearchValidationErrorDto>();
        using var document = Parse(body);
        foreach (var property in Properties(document.RootElement, errors))
        {
            if (property.Name.Equals(nameof(SmartSearchValidateRequestDto.Query), StringComparison.OrdinalIgnoreCase))
            {
                request.Read = ReadQuery(property.Value);
            }
            else
            {
                errors.Add(Error(property.Name, QueryErrorCodes.UnknownProperty, "A validate request has no such property."));
            }
        }

        Throw(errors);
        request.Read ??= MissingQuery();
        request.Query = request.Read.Definition;
        return request;
    }

    /// <summary>Reads the body that saves a query: a new one (<c>POST</c>) or a change (<c>PUT</c>).</summary>
    /// <param name="body">The body, UTF-8.</param>
    /// <param name="isChange">Whether it changes a stored query: <c>IfUpdatedAt</c> is required and
    /// <c>AcknowledgeAutoCollectionWarnings</c> and <c>DetachAutoCollections</c> allowed.</param>
    /// <returns>The request.</returns>
    /// <exception cref="PluginException">400 <c>InvalidRequest</c>, or 413.</exception>
    public static SmartSearchSavedQueryRequestDto ReadSave(ReadOnlySpan<byte> body, bool isChange)
    {
        var request = new SmartSearchSavedQueryRequestDto();
        var errors = new List<SmartSearchValidationErrorDto>();
        using var document = Parse(body, SmartSearchRequestLimits.MaxSaveBodyBytes);
        foreach (var property in Properties(document.RootElement, errors))
        {
            var value = property.Value;
            switch (property.Name.ToUpperInvariant())
            {
                case "NAME":
                    request.Name = Text(value, nameof(SmartSearchSavedQueryRequestDto.Name), errors);
                    break;
                case "DESCRIPTION":
                    request.Description = Text(value, nameof(SmartSearchSavedQueryRequestDto.Description), errors);
                    break;
                case "SHARED":
                    request.Shared = Bool(value, nameof(SmartSearchSavedQueryRequestDto.Shared), errors) ?? request.Shared;
                    break;
                case "QUERY":
                    request.Read = ReadQuery(value);
                    break;
                case "IFUPDATEDAT" when isChange:
                    if (value.ValueKind == JsonValueKind.String
                        && DateTime.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var updatedAt))
                    {
                        request.IfUpdatedAt = DateTime.SpecifyKind(updatedAt, DateTimeKind.Utc);
                    }
                    else
                    {
                        errors.Add(Error(nameof(SmartSearchSavedQueryRequestDto.IfUpdatedAt), QueryErrorCodes.InvalidValue, "IfUpdatedAt must be the query's UpdatedUtc, as read."));
                    }

                    break;
                case "ACKNOWLEDGEAUTOCOLLECTIONWARNINGS" when isChange:
                    request.AcknowledgeAutoCollectionWarnings = Bool(value, nameof(SmartSearchSavedQueryRequestDto.AcknowledgeAutoCollectionWarnings), errors) ?? false;
                    break;
                case "DETACHAUTOCOLLECTIONS" when isChange:
                    request.DetachAutoCollections = Bool(value, nameof(SmartSearchSavedQueryRequestDto.DetachAutoCollections), errors) ?? false;
                    break;
                default:
                    errors.Add(Error(property.Name, QueryErrorCodes.UnknownProperty, "This request has no such property."));
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(request.Name) && !errors.Exists(e => e.Path == nameof(SmartSearchSavedQueryRequestDto.Name)))
        {
            errors.Add(Error(nameof(SmartSearchSavedQueryRequestDto.Name), QueryErrorCodes.Required, "A saved query needs a name."));
        }

        if (isChange && request.IfUpdatedAt is null && !errors.Exists(e => e.Path == nameof(SmartSearchSavedQueryRequestDto.IfUpdatedAt)))
        {
            errors.Add(Error(nameof(SmartSearchSavedQueryRequestDto.IfUpdatedAt), QueryErrorCodes.Required, "A change needs IfUpdatedAt: the query's UpdatedUtc, as read."));
        }

        Throw(errors, "save request");
        request.Read ??= MissingQuery();
        request.Query = request.Read.Definition;
        return request;
    }

    /// <summary>Reads the body of a saved query's search; an empty body is the first page.</summary>
    /// <param name="body">The body, UTF-8.</param>
    /// <returns>The request.</returns>
    /// <exception cref="PluginException">400 <c>InvalidRequest</c>, or 413.</exception>
    public static SmartSearchSavedQueryRunDto ReadRun(ReadOnlySpan<byte> body)
    {
        var request = new SmartSearchSavedQueryRunDto();
        if (IsBlank(body))
        {
            return request;
        }

        var errors = new List<SmartSearchValidationErrorDto>();
        using var document = Parse(body);
        foreach (var property in Properties(document.RootElement, errors))
        {
            var value = property.Value;
            switch (property.Name.ToUpperInvariant())
            {
                case "STARTINDEX":
                    request.StartIndex = Int(value, nameof(SmartSearchSavedQueryRunDto.StartIndex), errors) ?? request.StartIndex;
                    break;
                case "LIMIT":
                    request.Limit = Int(value, nameof(SmartSearchSavedQueryRunDto.Limit), errors) ?? request.Limit;
                    break;
                case "RESULTTOKEN":
                    if (value.ValueKind is JsonValueKind.String or JsonValueKind.Null)
                    {
                        request.ResultToken = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                    }
                    else
                    {
                        errors.Add(Error(nameof(SmartSearchSavedQueryRunDto.ResultToken), QueryErrorCodes.InvalidValue, "ResultToken must be a string or null."));
                    }

                    break;
                case "INCLUDETOTAL":
                    request.IncludeTotal = Bool(value, nameof(SmartSearchSavedQueryRunDto.IncludeTotal), errors) ?? request.IncludeTotal;
                    break;
                default:
                    errors.Add(Error(property.Name, QueryErrorCodes.UnknownProperty, "A search request has no such property."));
                    break;
            }
        }

        if (request.StartIndex < 0)
        {
            errors.Add(Error(nameof(SmartSearchSavedQueryRunDto.StartIndex), QueryErrorCodes.OutOfRange, "StartIndex must be 0 or more."));
        }

        if (request.Limit < 1 || request.Limit > SmartSearchRequestLimits.MaxLimit)
        {
            errors.Add(Error(nameof(SmartSearchSavedQueryRunDto.Limit), QueryErrorCodes.OutOfRange, "Limit must be from 1 to 200."));
        }

        Throw(errors);
        return request;
    }

    /// <summary>Reads the body of a duplicate; an empty body keeps the original's name.</summary>
    /// <param name="body">The body, UTF-8.</param>
    /// <returns>The request.</returns>
    /// <exception cref="PluginException">400 <c>InvalidRequest</c>, or 413.</exception>
    public static SmartSearchSavedQueryDuplicateDto ReadDuplicate(ReadOnlySpan<byte> body)
    {
        var request = new SmartSearchSavedQueryDuplicateDto();
        if (IsBlank(body))
        {
            return request;
        }

        var errors = new List<SmartSearchValidationErrorDto>();
        using var document = Parse(body);
        foreach (var property in Properties(document.RootElement, errors))
        {
            if (property.Name.Equals(nameof(SmartSearchSavedQueryDuplicateDto.Name), StringComparison.OrdinalIgnoreCase))
            {
                request.Name = Text(property.Value, nameof(SmartSearchSavedQueryDuplicateDto.Name), errors);
            }
            else
            {
                errors.Add(Error(property.Name, QueryErrorCodes.UnknownProperty, "A duplicate request has no such property."));
            }
        }

        Throw(errors, "duplicate request");
        return request;
    }

    /// <summary>Reads the body that gives a query to another owner.</summary>
    /// <param name="body">The body, UTF-8.</param>
    /// <returns>The request.</returns>
    /// <exception cref="PluginException">400 <c>InvalidRequest</c>, or 413.</exception>
    public static SmartSearchSavedQueryOwnerDto ReadOwner(ReadOnlySpan<byte> body)
    {
        var request = new SmartSearchSavedQueryOwnerDto();
        var errors = new List<SmartSearchValidationErrorDto>();
        var found = false;
        using var document = Parse(body);
        foreach (var property in Properties(document.RootElement, errors))
        {
            if (property.Name.Equals(nameof(SmartSearchSavedQueryOwnerDto.UserId), StringComparison.OrdinalIgnoreCase))
            {
                found = true;
                if (Id(property.Value) is { } id)
                {
                    request.UserId = id;
                }
                else
                {
                    errors.Add(Error(nameof(SmartSearchSavedQueryOwnerDto.UserId), QueryErrorCodes.InvalidValue, "UserId must be a user's id."));
                }
            }
            else
            {
                errors.Add(Error(property.Name, QueryErrorCodes.UnknownProperty, "An owner request has no such property."));
            }
        }

        if (!found)
        {
            errors.Add(Error(nameof(SmartSearchSavedQueryOwnerDto.UserId), QueryErrorCodes.Required, "The request needs UserId."));
        }

        Throw(errors, "owner request");
        return request;
    }

    /// <summary>Reads the body of <i>Apply deleted-user policy</i>.</summary>
    /// <param name="body">The body, UTF-8.</param>
    /// <returns>The request.</returns>
    /// <exception cref="PluginException">400 <c>InvalidRequest</c>, or 413.</exception>
    public static SmartSearchApplyPolicyDto ReadApplyPolicy(ReadOnlySpan<byte> body)
    {
        var ids = new List<Guid>();
        var errors = new List<SmartSearchValidationErrorDto>();
        var found = false;
        using var document = Parse(body);
        foreach (var property in Properties(document.RootElement, errors))
        {
            if (!property.Name.Equals(nameof(SmartSearchApplyPolicyDto.UserIds), StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(Error(property.Name, QueryErrorCodes.UnknownProperty, "This request has no such property."));
                continue;
            }

            found = true;
            if (property.Value.ValueKind != JsonValueKind.Array)
            {
                errors.Add(Error(nameof(SmartSearchApplyPolicyDto.UserIds), QueryErrorCodes.InvalidValue, "UserIds must be a list of user ids."));
                continue;
            }

            var index = 0;
            foreach (var item in property.Value.EnumerateArray())
            {
                if (Id(item) is { } id)
                {
                    ids.Add(id);
                }
                else
                {
                    errors.Add(Error(nameof(SmartSearchApplyPolicyDto.UserIds) + "[" + index.ToString(CultureInfo.InvariantCulture) + "]", QueryErrorCodes.InvalidValue, "Every entry of UserIds must be a user id."));
                }

                index++;
            }
        }

        if (!found)
        {
            errors.Add(Error(nameof(SmartSearchApplyPolicyDto.UserIds), QueryErrorCodes.Required, "The request needs UserIds."));
        }

        Throw(errors, "request");
        return new SmartSearchApplyPolicyDto { UserIds = ids };
    }

    /// <summary>Reads the body of a name check: <c>Name</c>, and the <c>DefinitionId</c> it is for, if any.</summary>
    /// <param name="body">The body, UTF-8.</param>
    /// <returns>The request.</returns>
    /// <exception cref="PluginException">400 <c>InvalidRequest</c>, or 413.</exception>
    public static SmartSearchAutoCollectionCheckNameRequestDto ReadCheckName(ReadOnlySpan<byte> body)
    {
        var request = new SmartSearchAutoCollectionCheckNameRequestDto();
        var errors = new List<SmartSearchValidationErrorDto>();
        string? name = null;
        using var document = Parse(body);
        foreach (var property in Properties(document.RootElement, errors))
        {
            if (property.Name.Equals(nameof(SmartSearchAutoCollectionCheckNameRequestDto.Name), StringComparison.OrdinalIgnoreCase))
            {
                name = Text(property.Value, nameof(SmartSearchAutoCollectionCheckNameRequestDto.Name), errors);
            }
            else if (property.Name.Equals(nameof(SmartSearchAutoCollectionCheckNameRequestDto.DefinitionId), StringComparison.OrdinalIgnoreCase))
            {
                if (property.Value.ValueKind == JsonValueKind.Null)
                {
                    continue;
                }

                if (Id(property.Value) is { } id)
                {
                    request.DefinitionId = id;
                }
                else
                {
                    errors.Add(Error(nameof(SmartSearchAutoCollectionCheckNameRequestDto.DefinitionId), QueryErrorCodes.InvalidValue, "DefinitionId must be an auto-collection's id."));
                }
            }
            else
            {
                errors.Add(Error(property.Name, QueryErrorCodes.UnknownProperty, "A name check has no such property."));
            }
        }

        if (name is null && !errors.Exists(e => e.Path == nameof(SmartSearchAutoCollectionCheckNameRequestDto.Name)))
        {
            errors.Add(Error(nameof(SmartSearchAutoCollectionCheckNameRequestDto.Name), QueryErrorCodes.Required, "The request needs Name."));
        }

        Throw(errors, "name check");
        request.Name = name ?? string.Empty;
        return request;
    }

    /// <summary>
    /// Reads the body of an auto-collection preview: <c>Definition</c> (a new one, which needs a source) and, for a saved
    /// one, <c>DefinitionId</c> with the changes in <c>Definition</c>.
    /// </summary>
    /// <param name="body">The body, UTF-8.</param>
    /// <returns>The request.</returns>
    /// <exception cref="PluginException">400 <c>InvalidRequest</c>, or 413.</exception>
    public static SmartSearchAutoCollectionPreviewRequestDto ReadPreview(ReadOnlySpan<byte> body)
    {
        var request = new SmartSearchAutoCollectionPreviewRequestDto();
        var errors = new List<SmartSearchValidationErrorDto>();
        using var document = Parse(body, SmartSearchRequestLimits.MaxSaveBodyBytes);
        foreach (var property in Properties(document.RootElement, errors))
        {
            var value = property.Value;
            switch (property.Name.ToUpperInvariant())
            {
                case "DEFINITIONID":
                    request.DefinitionId = OptionalId(value, nameof(SmartSearchAutoCollectionPreviewRequestDto.DefinitionId), errors);
                    break;
                case "DEFINITION":
                    request.Definition = ReadDefinition(value, nameof(SmartSearchAutoCollectionPreviewRequestDto.Definition), errors);
                    break;
                default:
                    errors.Add(Error(property.Name, QueryErrorCodes.UnknownProperty, "A preview request has no such property."));
                    break;
            }
        }

        if (request.DefinitionId is null && request.Definition?.Source is null && !errors.Exists(e => e.Path.StartsWith(nameof(SmartSearchAutoCollectionPreviewRequestDto.Definition), StringComparison.Ordinal)))
        {
            errors.Add(Error(nameof(SmartSearchAutoCollectionPreviewRequestDto.Definition) + ".Source", QueryErrorCodes.Required, "A new auto-collection needs Definition.Source: a saved query or a query."));
        }

        Throw(errors, "preview request");
        return request;
    }

    /// <summary>
    /// Reads the body of a new auto-collection (<c>Definition</c> with a name and a source, and
    /// <c>AcceptedPreviewHash</c>) or of a change (<c>Definition</c> with what changes, and <c>IfUpdatedAt</c>).
    /// </summary>
    /// <param name="body">The body, UTF-8.</param>
    /// <param name="isChange">Whether it changes a saved one.</param>
    /// <returns>The request.</returns>
    /// <exception cref="PluginException">400 <c>InvalidRequest</c>, or 413.</exception>
    public static SmartSearchAutoCollectionRequestDto ReadAutoCollection(ReadOnlySpan<byte> body, bool isChange)
    {
        var request = new SmartSearchAutoCollectionRequestDto();
        var errors = new List<SmartSearchValidationErrorDto>();
        using var document = Parse(body, SmartSearchRequestLimits.MaxSaveBodyBytes);
        foreach (var property in Properties(document.RootElement, errors))
        {
            var value = property.Value;
            switch (property.Name.ToUpperInvariant())
            {
                case "DEFINITION":
                    request.Definition = ReadDefinition(value, nameof(SmartSearchAutoCollectionRequestDto.Definition), errors);
                    break;
                case "ACCEPTEDPREVIEWHASH":
                    request.AcceptedPreviewHash = Text(value, nameof(SmartSearchAutoCollectionRequestDto.AcceptedPreviewHash), errors);
                    break;
                case "DUPLICATESOURCEQUERY":
                    request.DuplicateSourceQuery = Bool(value, nameof(SmartSearchAutoCollectionRequestDto.DuplicateSourceQuery), errors);
                    break;
                case "IFUPDATEDAT" when isChange:
                    request.IfUpdatedAt = Utc(value, nameof(SmartSearchAutoCollectionRequestDto.IfUpdatedAt), "IfUpdatedAt must be the auto-collection's UpdatedUtc, as read.", errors);
                    break;
                default:
                    errors.Add(Error(property.Name, QueryErrorCodes.UnknownProperty, "This request has no such property."));
                    break;
            }
        }

        var definitionErrors = errors.Exists(e => e.Path.StartsWith(nameof(SmartSearchAutoCollectionRequestDto.Definition), StringComparison.Ordinal));
        if (request.Definition is null && !definitionErrors)
        {
            errors.Add(Error(nameof(SmartSearchAutoCollectionRequestDto.Definition), QueryErrorCodes.Required, "The request needs Definition."));
        }
        else if (!isChange && request.Definition is { } definition && !definitionErrors)
        {
            if (string.IsNullOrWhiteSpace(definition.Name))
            {
                errors.Add(Error("Definition.Name", QueryErrorCodes.Required, "An auto-collection needs a name."));
            }

            if (definition.Source is null)
            {
                errors.Add(Error("Definition.Source", QueryErrorCodes.Required, "An auto-collection needs a source: a saved query or a query."));
            }
        }

        if (!isChange && string.IsNullOrWhiteSpace(request.AcceptedPreviewHash) && !errors.Exists(e => e.Path == nameof(SmartSearchAutoCollectionRequestDto.AcceptedPreviewHash)))
        {
            errors.Add(Error(nameof(SmartSearchAutoCollectionRequestDto.AcceptedPreviewHash), QueryErrorCodes.Required, "A new auto-collection needs AcceptedPreviewHash: the PreviewHash of the preview it was saved from."));
        }

        if (isChange && request.IfUpdatedAt is null && !errors.Exists(e => e.Path == nameof(SmartSearchAutoCollectionRequestDto.IfUpdatedAt)))
        {
            errors.Add(Error(nameof(SmartSearchAutoCollectionRequestDto.IfUpdatedAt), QueryErrorCodes.Required, "A change needs IfUpdatedAt: the auto-collection's UpdatedUtc, as read."));
        }

        Throw(errors, "auto-collection request");
        return request;
    }

    /// <summary>Reads the body of a recreate; an empty body keeps the name.</summary>
    /// <param name="body">The body, UTF-8.</param>
    /// <returns>The request.</returns>
    /// <exception cref="PluginException">400 <c>InvalidRequest</c>, or 413.</exception>
    public static SmartSearchAutoCollectionRecreateRequestDto ReadRecreate(ReadOnlySpan<byte> body)
    {
        var request = new SmartSearchAutoCollectionRecreateRequestDto();
        if (IsBlank(body))
        {
            return request;
        }

        var errors = new List<SmartSearchValidationErrorDto>();
        using var document = Parse(body);
        foreach (var property in Properties(document.RootElement, errors))
        {
            if (property.Name.Equals(nameof(SmartSearchAutoCollectionRecreateRequestDto.Name), StringComparison.OrdinalIgnoreCase))
            {
                request.Name = Text(property.Value, nameof(SmartSearchAutoCollectionRecreateRequestDto.Name), errors);
            }
            else
            {
                errors.Add(Error(property.Name, QueryErrorCodes.UnknownProperty, "A recreate request has no such property."));
            }
        }

        Throw(errors, "recreate request");
        return request;
    }

    /// <summary>Reads the body of <c>Guard/Apply</c> and <c>Guard/Keep</c>: <c>DetectedUtc</c>, required.</summary>
    /// <param name="body">The body, UTF-8.</param>
    /// <returns>The request.</returns>
    /// <exception cref="PluginException">400 <c>InvalidRequest</c>, or 413.</exception>
    public static SmartSearchAutoCollectionGuardRequestDto ReadGuard(ReadOnlySpan<byte> body)
    {
        var request = new SmartSearchAutoCollectionGuardRequestDto();
        var errors = new List<SmartSearchValidationErrorDto>();
        using var document = Parse(body);
        foreach (var property in Properties(document.RootElement, errors))
        {
            if (property.Name.Equals(nameof(SmartSearchAutoCollectionGuardRequestDto.DetectedUtc), StringComparison.OrdinalIgnoreCase))
            {
                request.DetectedUtc = Utc(property.Value, nameof(SmartSearchAutoCollectionGuardRequestDto.DetectedUtc), "DetectedUtc must be the held removals' DetectedUtc, as read.", errors);
            }
            else
            {
                errors.Add(Error(property.Name, QueryErrorCodes.UnknownProperty, "A guard request has no such property."));
            }
        }

        if (request.DetectedUtc is null && !errors.Exists(e => e.Path == nameof(SmartSearchAutoCollectionGuardRequestDto.DetectedUtc)))
        {
            errors.Add(Error(nameof(SmartSearchAutoCollectionGuardRequestDto.DetectedUtc), QueryErrorCodes.Required, "The request needs DetectedUtc: the held removals' DetectedUtc, as read."));
        }

        Throw(errors, "guard request");
        return request;
    }

    // An auto-collection's settings, every part optional; each name once, none unknown, the paths prefixed.
    private static SmartSearchAutoCollectionDefinitionDto? ReadDefinition(JsonElement value, string path, List<SmartSearchValidationErrorDto> errors)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            errors.Add(Error(path, QueryErrorCodes.NotAnObject, path + " must be an object."));
            return null;
        }

        var definition = new SmartSearchAutoCollectionDefinitionDto();
        foreach (var property in Properties(value, errors, path + "."))
        {
            var inner = property.Value;
            var at = path + "." + property.Name;
            switch (property.Name.ToUpperInvariant())
            {
                case "NAME":
                    definition.Name = Text(inner, at, errors);
                    break;
                case "ENABLED":
                    definition.Enabled = Bool(inner, at, errors);
                    break;
                case "EVALUATIONUSERID":
                    definition.HasEvaluationUserId = true;
                    definition.EvaluationUserId = OptionalId(inner, at, errors);
                    break;
                case "SOURCE":
                    definition.Source = ReadSource(inner, at, errors);
                    break;
                case "PRESENTATION":
                    definition.Presentation = ReadPresentation(inner, at, errors);
                    break;
                case "MANUALEDITS":
                    definition.ManualEdits = ReadManualEdits(inner, at, errors);
                    break;
                case "SAFETY":
                    definition.Safety = ReadSafety(inner, at, errors);
                    break;
                default:
                    errors.Add(Error(at, QueryErrorCodes.UnknownProperty, "An auto-collection has no such property."));
                    break;
            }
        }

        return definition;
    }

    private static SmartSearchAutoCollectionSourceDto? ReadSource(JsonElement value, string path, List<SmartSearchValidationErrorDto> errors)
    {
        if (!IsObject(value, path, errors))
        {
            return null;
        }

        var source = new SmartSearchAutoCollectionSourceDto();
        foreach (var property in Properties(value, errors, path + "."))
        {
            var inner = property.Value;
            var at = path + "." + property.Name;
            switch (property.Name.ToUpperInvariant())
            {
                case "MODE":
                    source.Mode = Choice(inner, at, errors, "Reference", "Snapshot");
                    break;
                case "SAVEDQUERYID":
                    source.SavedQueryId = OptionalId(inner, at, errors);
                    break;
                case "QUERY":
                    if (inner.ValueKind != JsonValueKind.Null)
                    {
                        source.Read = ReadQuery(inner);
                        source.Query = source.Read.Definition;
                    }

                    break;
                default:
                    errors.Add(Error(at, QueryErrorCodes.UnknownProperty, "A source has no such property."));
                    break;
            }
        }

        var mode = source.Mode ?? (source.SavedQueryId is not null ? "Reference" : source.Read is not null ? "Snapshot" : null);
        if (mode is null && !errors.Exists(e => e.Path.StartsWith(path + ".", StringComparison.Ordinal)))
        {
            errors.Add(Error(path + ".SavedQueryId", QueryErrorCodes.Required, "A source needs SavedQueryId or Query."));
        }
        else if (mode == "Reference" && (source.SavedQueryId is null || source.Read is not null))
        {
            errors.Add(Error(path + ".SavedQueryId", QueryErrorCodes.InvalidValue, "A Reference source names a saved query (SavedQueryId) and no Query."));
        }
        else if (mode == "Snapshot" && (source.Read is null || source.SavedQueryId is not null))
        {
            errors.Add(Error(path + ".Query", QueryErrorCodes.InvalidValue, "A Snapshot source gives a Query and no SavedQueryId."));
        }

        source.Mode = mode;
        return source;
    }

    private static SmartSearchAutoCollectionPresentationDto? ReadPresentation(JsonElement value, string path, List<SmartSearchValidationErrorDto> errors)
    {
        if (!IsObject(value, path, errors))
        {
            return null;
        }

        var presentation = new SmartSearchAutoCollectionPresentationDto();
        foreach (var property in Properties(value, errors, path + "."))
        {
            var inner = property.Value;
            var at = path + "." + property.Name;
            switch (property.Name.ToUpperInvariant())
            {
                case "SORTMODE":
                    presentation.SortMode = Choice(inner, at, errors, "QueryOrder", "PremiereDate", "SortName");
                    break;
                case "LOCK":
                    presentation.Lock = Bool(inner, at, errors);
                    break;
                case "OVERVIEWMODE":
                    presentation.OverviewMode = Choice(inner, at, errors, "LeaveAsIs", "Managed");
                    break;
                case "OVERVIEW":
                    presentation.Overview = Text(inner, at, errors);
                    break;
                default:
                    errors.Add(Error(at, QueryErrorCodes.UnknownProperty, "A presentation has no such property."));
                    break;
            }
        }

        return presentation;
    }

    private static SmartSearchAutoCollectionManualEditsDto? ReadManualEdits(JsonElement value, string path, List<SmartSearchValidationErrorDto> errors)
    {
        if (!IsObject(value, path, errors))
        {
            return null;
        }

        var edits = new SmartSearchAutoCollectionManualEditsDto();
        foreach (var property in Properties(value, errors, path + "."))
        {
            var inner = property.Value;
            var at = path + "." + property.Name;
            switch (property.Name.ToUpperInvariant())
            {
                case "MODE":
                    edits.Mode = Choice(inner, at, errors, "Respect", "Overwrite");
                    break;
                case "PINNEDITEMIDS":
                    edits.PinnedItemIds = Ids(inner, at, errors);
                    break;
                case "EXCLUDEDITEMIDS":
                    edits.ExcludedItemIds = Ids(inner, at, errors);
                    break;
                default:
                    errors.Add(Error(at, QueryErrorCodes.UnknownProperty, "Manual edits have no such property."));
                    break;
            }
        }

        return edits;
    }

    private static SmartSearchAutoCollectionSafetyDto? ReadSafety(JsonElement value, string path, List<SmartSearchValidationErrorDto> errors)
    {
        if (!IsObject(value, path, errors))
        {
            return null;
        }

        var safety = new SmartSearchAutoCollectionSafetyDto();
        foreach (var property in Properties(value, errors, path + "."))
        {
            var inner = property.Value;
            var at = path + "." + property.Name;
            switch (property.Name.ToUpperInvariant())
            {
                case "ALLOWEMPTY":
                    safety.AllowEmpty = Bool(inner, at, errors);
                    break;
                case "MAXREMOVALPERCENT":
                    safety.HasMaxRemovalPercent = true;
                    if (inner.ValueKind != JsonValueKind.Null)
                    {
                        safety.MaxRemovalPercent = Int(inner, at, errors);
                        if (safety.MaxRemovalPercent is < 1 or > 100)
                        {
                            errors.Add(Error(at, QueryErrorCodes.OutOfRange, "MaxRemovalPercent must be from 1 to 100, or null for the server's setting."));
                        }
                    }

                    break;
                default:
                    errors.Add(Error(at, QueryErrorCodes.UnknownProperty, "Safety has no such property."));
                    break;
            }
        }

        return safety;
    }

    private static bool IsObject(JsonElement value, string path, List<SmartSearchValidationErrorDto> errors)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        errors.Add(Error(path, QueryErrorCodes.NotAnObject, path + " must be an object."));
        return false;
    }

    private static string? Choice(JsonElement value, string name, List<SmartSearchValidationErrorDto> errors, params string[] allowed)
    {
        var text = Text(value, name, errors);
        if (text is null)
        {
            return null;
        }

        var match = Array.Find(allowed, a => a.Equals(text, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            errors.Add(Error(name, QueryErrorCodes.InvalidValue, name + " must be one of " + string.Join(", ", allowed) + "."));
        }

        return match;
    }

    private static DateTime? Utc(JsonElement value, string name, string message, List<SmartSearchValidationErrorDto> errors)
    {
        if (value.ValueKind == JsonValueKind.String
            && DateTime.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
        }

        errors.Add(Error(name, QueryErrorCodes.InvalidValue, message));
        return null;
    }

    private static Guid? OptionalId(JsonElement value, string name, List<SmartSearchValidationErrorDto> errors)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        var id = Id(value);
        if (id is null)
        {
            errors.Add(Error(name, QueryErrorCodes.InvalidValue, name + " must be an id."));
        }

        return id;
    }

    private static List<Guid>? Ids(JsonElement value, string name, List<SmartSearchValidationErrorDto> errors)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            errors.Add(Error(name, QueryErrorCodes.InvalidValue, name + " must be a list of item ids."));
            return null;
        }

        var ids = new List<Guid>();
        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            if (Id(item) is { } id)
            {
                ids.Add(id);
            }
            else
            {
                errors.Add(Error(name + "[" + index.ToString(CultureInfo.InvariantCulture) + "]", QueryErrorCodes.InvalidValue, "Every entry of " + name + " must be an item id."));
            }

            index++;
        }

        return ids;
    }

    private static async Task<byte[]> ReadBodyAsync(Stream body, int maxBytes, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                throw PluginException.PayloadTooLarge(TooLargeMessage);
            }

            buffer.Write(chunk.AsSpan(0, read));
        }

        return buffer.ToArray();
    }

    private static JsonDocument Parse(ReadOnlySpan<byte> body, int maxBytes = SmartSearchRequestLimits.MaxBodyBytes)
    {
        if (body.Length > maxBytes)
        {
            throw PluginException.PayloadTooLarge(TooLargeMessage);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body.ToArray(), new JsonDocumentOptions { MaxDepth = 64 });
        }
        catch (JsonException)
        {
            throw Invalid([Error(string.Empty, QueryErrorCodes.InvalidJson, "The request is not valid JSON.")]);
        }

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            throw Invalid([Error(string.Empty, QueryErrorCodes.NotAnObject, "The request is not a JSON object.")]);
        }

        return document;
    }

    // The body's properties, each name once (ignoring case); a repeated one is an error and is skipped.
    private static List<JsonProperty> Properties(JsonElement root, List<SmartSearchValidationErrorDto> errors, string prefix = "")
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var properties = new List<JsonProperty>();
        foreach (var property in root.EnumerateObject())
        {
            if (seen.Add(property.Name))
            {
                properties.Add(property);
            }
            else
            {
                errors.Add(Error(prefix + property.Name, QueryErrorCodes.InvalidJson, "A property is given twice."));
            }
        }

        return properties;
    }

    private static QueryReadResult ReadQuery(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => QueryJson.Read(System.Text.Encoding.UTF8.GetBytes(value.GetRawText())),
        JsonValueKind.Null => MissingQuery(),
        _ => new QueryReadResult(null, [new QueryValidationError(string.Empty, QueryErrorCodes.NotAnObject, "The query is not a JSON object.")], null)
    };

    private static QueryReadResult MissingQuery()
        => new(null, [new QueryValidationError(string.Empty, QueryErrorCodes.Required, "The request has no query.")], null);

    private static int? Int(JsonElement value, string name, List<SmartSearchValidationErrorDto> errors)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }

        errors.Add(Error(name, QueryErrorCodes.InvalidValue, name + " must be a whole number."));
        return null;
    }

    private static bool IsBlank(ReadOnlySpan<byte> body)
    {
        foreach (var b in body)
        {
            if (b is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
            {
                return false;
            }
        }

        return true;
    }

    private static string? Text(JsonElement value, string name, List<SmartSearchValidationErrorDto> errors)
    {
        if (value.ValueKind is JsonValueKind.String or JsonValueKind.Null)
        {
            return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }

        errors.Add(Error(name, QueryErrorCodes.InvalidValue, name + " must be text."));
        return null;
    }

    private static bool? Bool(JsonElement value, string name, List<SmartSearchValidationErrorDto> errors)
    {
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return value.GetBoolean();
        }

        errors.Add(Error(name, QueryErrorCodes.InvalidValue, name + " must be true or false."));
        return null;
    }

    private static Guid? Id(JsonElement value)
        => value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var id) && !id.Equals(Guid.Empty) ? id : null;

    private static SmartSearchValidationErrorDto Error(string path, string code, string message) => new() { Path = path, Code = code, Message = message };

    private static void Throw(List<SmartSearchValidationErrorDto> errors, string what = "search request")
    {
        if (errors.Count > 0)
        {
            throw Invalid(errors, what);
        }
    }

    private static PluginException Invalid(IReadOnlyList<SmartSearchValidationErrorDto> errors, string what = "search request")
        => new(PluginErrorKind.Invalid, "The request isn't a valid " + what + ": " + string.Join(" ", errors.Select(e => e.Message).Distinct()))
        {
            Code = InvalidRequestCode,
            Extensions = new Dictionary<string, object?> { ["errors"] = ProblemError.Of(errors) }
        };
}
