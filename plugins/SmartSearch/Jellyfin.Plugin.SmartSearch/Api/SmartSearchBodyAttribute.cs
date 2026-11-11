using System;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jellyfin.Plugin.SmartSearch.Api;

/// <summary>
/// Binds a search or validate request's JSON body with <see cref="SmartSearchRequestBinder"/>, which reads the query as
/// strictly as a saved one. The binding source stays <c>Body</c>, so the API description (and Jellyfin's OpenAPI document)
/// shows the request's schema as for <c>[FromBody]</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter)]
internal sealed class SmartSearchBodyAttribute : ModelBinderAttribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SmartSearchBodyAttribute"/> class.
    /// </summary>
    public SmartSearchBodyAttribute()
        : base(typeof(SmartSearchRequestBinder))
    {
        BindingSource = BindingSource.Body;
    }
}
