using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Jellyfin.Plugin.AccessRoles.Rules;

/// <summary>
/// One controller action as the <see cref="ActionManifest"/> records it and its checks compare it: the controller's full
/// name and MVC's action name (the rule identity, stable across route aliases and BaseUrl), its routes with
/// their HTTP methods, its <c>[Authorize]</c> policies, whether it allows anonymous callers outright, and every parameter
/// MVC binds (the arguments the rules read, and among them the scope parameters the generic backstop looks
/// at). The manifest test builds these from a <c>Jellyfin.Api.dll</c>'s metadata and the running server's from its
/// action descriptors (<see cref="ServerActions"/>), the same way.
/// </summary>
internal sealed class ApiActionShape
{
    /// <summary>The policy name written for an <c>[Authorize]</c> without a policy: Jellyfin's default policy.</summary>
    public const string DefaultPolicy = "Default";

    /// <summary>The method list written for a route that takes any HTTP method.</summary>
    public const string AnyMethod = "*";

    /// <summary>Separates a parameter's name from the name it is bound from, when an attribute names that (<see cref="FormatParameter"/>).</summary>
    public const char BindingNameSeparator = '=';

    /// <summary>The parameter names (any case) the generic backstop looks at.</summary>
    public static readonly IReadOnlyList<string> ScopeParameterNames = ["parentId", "seriesId", "seasonId"];

    /// <summary>
    /// Initializes a new instance of the <see cref="ApiActionShape"/> class. The lists are sorted and made distinct, so two
    /// shapes of the same action compare equal whatever order their sources gave.
    /// </summary>
    /// <param name="controller">The controller's full type name, for example <c>Jellyfin.Api.Controllers.UserController</c>.</param>
    /// <param name="action">MVC's action name, for example <c>UpdateUserPassword</c>.</param>
    /// <param name="routes">The routes, each <c>"&lt;METHODS&gt; &lt;template&gt;"</c> (<see cref="FormatRoute"/>).</param>
    /// <param name="policies">The <c>[Authorize]</c> policies of the controller and the action; <see cref="DefaultPolicy"/> for none named.</param>
    /// <param name="allowAnonymous">Whether the controller or the action carries <c>[AllowAnonymous]</c>.</param>
    /// <param name="parameters">Every parameter of the action method, each as <see cref="FormatParameter"/> writes it.</param>
    public ApiActionShape(string controller, string action, IEnumerable<string> routes, IEnumerable<string> policies, bool allowAnonymous, IEnumerable<string> parameters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(controller);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(policies);
        ArgumentNullException.ThrowIfNull(parameters);

        Controller = controller;
        Action = action;
        Routes = Normalize(routes);
        Policies = Normalize(policies);
        AllowAnonymous = allowAnonymous;
        Parameters = Normalize(parameters);
        ScopeParameters = ScopeParametersOf(Parameters);
    }

    /// <summary>Gets the controller's full type name.</summary>
    public string Controller { get; }

    /// <summary>Gets MVC's action name.</summary>
    public string Action { get; }

    /// <summary>Gets the routes, sorted ordinally.</summary>
    public IReadOnlyList<string> Routes { get; }

    /// <summary>Gets the policies, sorted ordinally; empty when nothing asks for authorization.</summary>
    public IReadOnlyList<string> Policies { get; }

    /// <summary>Gets a value indicating whether <c>[AllowAnonymous]</c> opens the action.</summary>
    public bool AllowAnonymous { get; }

    /// <summary>
    /// Gets every parameter MVC binds for the action, sorted ordinally, each as <see cref="FormatParameter"/> writes it: the
    /// name a filter finds the argument under (<c>ActionExecutingContext.ActionArguments</c>), so a rename in Jellyfin shows
    /// as a changed action instead of a rule that silently no longer finds its argument (the rewrite rules pass
    /// through when their argument is missing).
    /// </summary>
    public IReadOnlyList<string> Parameters { get; }

    /// <summary>Gets the names the scope parameters are bound from (<see cref="ScopeParameterNames"/>), sorted ordinally.</summary>
    public IReadOnlyList<string> ScopeParameters { get; }

    /// <summary>
    /// Gets a value indicating whether a caller without a token reaches the action: <c>[AllowAnonymous]</c>, or no
    /// <c>[Authorize]</c> at all (Jellyfin sets no fallback policy). Policies that admit LAN or first-run
    /// callers (<c>AnonymousLanAccessPolicy</c>, <c>LocalAccessOrRequiresElevation</c>, <c>FirstTimeSetup*</c>) are not
    /// counted here; the manifest's reviews name them.
    /// </summary>
    public bool IsAnonymous => AllowAnonymous || Policies.Count == 0;

    /// <summary>Gets the key the manifest and the rule table use: <c>&lt;Controller&gt;.&lt;Action&gt;</c>.</summary>
    public string Key => Controller + "." + Action;

    /// <summary>
    /// Writes one route: the HTTP methods in upper case, sorted and joined by commas (<see cref="AnyMethod"/> for none),
    /// a space, and the template MVC routes (combined with the controller's and its tokens replaced).
    /// </summary>
    /// <param name="methods">The HTTP methods; empty for any.</param>
    /// <param name="template">The route template.</param>
    /// <returns>The route.</returns>
    public static string FormatRoute(IEnumerable<string> methods, string? template)
    {
        ArgumentNullException.ThrowIfNull(methods);
        var verbs = methods.Select(m => m.ToUpperInvariant()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        return string.Create(CultureInfo.InvariantCulture, $"{(verbs.Count == 0 ? AnyMethod : string.Join(',', verbs))} {template ?? string.Empty}");
    }

    /// <summary>
    /// Writes one parameter: its name (MVC's <c>ParameterDescriptor.Name</c>, the key of the action's arguments), and when
    /// an attribute binds it from another name (<c>[FromQuery(Name = …)]</c> and the like, MVC's
    /// <c>BindingInfo.BinderModelName</c>) <see cref="BindingNameSeparator"/> and that name.
    /// </summary>
    /// <param name="name">The parameter's name.</param>
    /// <param name="bindingName">The name it is bound from, or <c>null</c> (or empty) for its own.</param>
    /// <returns>The parameter, for example <c>parentId</c> or <c>series=SeriesId</c>.</returns>
    public static string FormatParameter(string name, string? bindingName)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return string.IsNullOrEmpty(bindingName) || string.Equals(name, bindingName, StringComparison.Ordinal)
            ? name
            : name + BindingNameSeparator + bindingName;
    }

    /// <summary>
    /// Reads the name of a parameter written by <see cref="FormatParameter"/>: the key of its argument.
    /// </summary>
    /// <param name="parameter">The parameter as written.</param>
    /// <returns>The parameter's name.</returns>
    public static string ArgumentName(string parameter)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        var separator = parameter.IndexOf(BindingNameSeparator, StringComparison.Ordinal);
        return separator < 0 ? parameter : parameter[..separator];
    }

    /// <summary>
    /// Reads the name a parameter written by <see cref="FormatParameter"/> is bound from (what a client sends).
    /// </summary>
    /// <param name="parameter">The parameter as written.</param>
    /// <returns>The binding name, or the parameter's name when no attribute names another.</returns>
    public static string BoundName(string parameter)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        var separator = parameter.IndexOf(BindingNameSeparator, StringComparison.Ordinal);
        return separator < 0 ? parameter : parameter[(separator + 1)..];
    }

    /// <summary>
    /// Lists the scope parameters among parameters written by <see cref="FormatParameter"/>.
    /// </summary>
    /// <param name="parameters">The parameters.</param>
    /// <returns>The names they are bound from that are a <see cref="ScopeParameterNames"/> name, sorted ordinally.</returns>
    public static IReadOnlyList<string> ScopeParametersOf(IEnumerable<string> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return Normalize(parameters.Select(BoundName).Where(IsScopeParameter));
    }

    /// <summary>
    /// Gets a value indicating whether a bound parameter name is one the generic backstop looks at.
    /// </summary>
    /// <param name="name">The name MVC binds the parameter from.</param>
    /// <returns><c>true</c> for <c>parentId</c>, <c>seriesId</c> or <c>seasonId</c> in any case.</returns>
    public static bool IsScopeParameter(string? name)
        => name is not null && ScopeParameterNames.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Lists how another shape of the same action (a DLL's, or the running server's) differs from this one (as reviewed),
    /// for a check's message and a candidate manifest's review.
    /// </summary>
    /// <param name="other">The other shape.</param>
    /// <returns>One line per differing part, <c>"&lt;part&gt;: reviewed […], now […]"</c>; empty when they agree.</returns>
    public IReadOnlyList<string> DescribeDifferences(ApiActionShape other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var differences = new List<string>();
        Compare("routes", Routes, other.Routes, differences);
        Compare("policies", Policies, other.Policies, differences);
        if (AllowAnonymous != other.AllowAnonymous)
        {
            differences.Add(string.Create(CultureInfo.InvariantCulture, $"[AllowAnonymous]: reviewed {AllowAnonymous}, now {other.AllowAnonymous}"));
        }

        Compare("parameters", Parameters, other.Parameters, differences);
        return differences;
    }

    /// <inheritdoc />
    public override string ToString() => Key;

    private static void Compare(string what, IReadOnlyList<string> reviewed, IReadOnlyList<string> now, List<string> differences)
    {
        if (!reviewed.SequenceEqual(now, StringComparer.Ordinal))
        {
            differences.Add($"{what}: reviewed [{string.Join("; ", reviewed)}], now [{string.Join("; ", now)}]");
        }
    }

    private static List<string> Normalize(IEnumerable<string> values)
        => values.Where(v => !string.IsNullOrEmpty(v)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
}
