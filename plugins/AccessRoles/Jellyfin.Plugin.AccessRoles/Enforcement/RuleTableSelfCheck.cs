using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AccessRoles.Rules;
using Jellyfin.Plugin.Kit.Hosting;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jellyfin.Plugin.AccessRoles.Enforcement;

/// <summary>
/// The <c>RuleTableSelfCheck</c>: a kit background service, so it waits for the startup gate and never
/// stops the host, that checks the rule table against the Jellyfin actually running, once: the filter is registered,
/// every manifest action with a rule binds to an action the server routes with the shape it was reviewed with, and every
/// action outside the manifest is listed as unreviewed (new Jellyfin.Api actions, new <c>LiveTvManagement</c> ones among
/// them, Jellyfin Enhanced <c>user-settings</c> saves not reviewed, and plugin controllers, informational, except those
/// whose constructors take a Jellyfin controller, which are flagged like <c>TrailersController</c>). Since 0.9.6
/// it also lists every authenticated write of Jellyfin Enhanced's that no rule covers
/// (<c>JellyfinEnhanced.Uncovered</c>, with its methods and route; every method but <c>GET</c>,
/// <c>HEAD</c> and <c>OPTIONS</c>, on every controller of JE's assembly), those that answer only administrators
/// (<c>AdminOnly</c>: a policy that requires one, or JE's own check, reviewed), and the rest nobody reviewed
/// (<c>UncoveredUnreviewed</c>, a signal: empty on JE 12.14): a new JE write outside <c>user-settings/{userId}/</c>, such as
/// 12.14's <c>spoiler-blur/user-prefs</c> was, is then seen.
/// </summary>
internal sealed class RuleTableSelfCheck : KitBackgroundService
{
    /// <summary>The namespace of Jellyfin's controllers.</summary>
    public const string JellyfinControllers = "Jellyfin.Api.Controllers.";

    private readonly ActionRuleTable _table;
    private readonly IActionDescriptorCollectionProvider? _descriptors;
    private readonly IOptions<MvcOptions>? _mvc;
    private IDictionary<string, object?>? _result;

    /// <summary>
    /// Initializes a new instance of the <see cref="RuleTableSelfCheck"/> class.
    /// </summary>
    /// <param name="applicationHost">The server, for the startup gate.</param>
    /// <param name="table">The rule table.</param>
    /// <param name="descriptors">MVC's actions; <c>null</c> without MVC (tests).</param>
    /// <param name="mvc">MVC's options, for the filter's registration; <c>null</c> without MVC.</param>
    /// <param name="logger">The logger.</param>
    public RuleTableSelfCheck(IServerApplicationHost applicationHost, ActionRuleTable table, IActionDescriptorCollectionProvider? descriptors, IOptions<MvcOptions>? mvc, ILogger logger)
        : base(applicationHost, logger)
    {
        ArgumentNullException.ThrowIfNull(table);
        _table = table;
        _descriptors = descriptors;
        _mvc = mvc;
    }

    /// <summary>
    /// Gets Jellyfin's authorization policies that only an administrator (or an API key) meets once the server is set up
    /// (<c>v10.11.5:MediaBrowser.Common/Api/Policies.cs</c>): an action under one needs no lock, since no lock applies to an
    /// administrator. <c>LocalAccessOrRequiresElevation</c> isn't one (a local caller meets it).
    /// </summary>
    public static IReadOnlySet<string> AdministratorPolicies { get; } = new HashSet<string>(StringComparer.Ordinal) { "RequiresElevation", "FirstTimeSetupOrElevated" };

    /// <summary>Gets the last check's result, or <c>null</c> before it ran.</summary>
    public IDictionary<string, object?>? Result => Volatile.Read(ref _result);

    /// <summary>
    /// Checks the rule table against a set of actions (the running server's).
    /// </summary>
    /// <param name="table">The rule table.</param>
    /// <param name="descriptors">The actions MVC routes.</param>
    /// <param name="filterRegistered">Whether the global filter is among MVC's filters.</param>
    /// <param name="checkedUtc">When.</param>
    /// <returns>The result, for Diagnostics' <c>RuleTable</c> entry.</returns>
    public static IDictionary<string, object?> Check(ActionRuleTable table, IReadOnlyList<ControllerActionDescriptor> descriptors, bool filterRegistered, DateTime checkedUtc)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(descriptors);
        var manifest = table.Manifest;
        var api = descriptors.Where(d => string.Equals(d.ControllerTypeInfo.Assembly.GetName().Name, ActionRuleTable.JellyfinApiAssembly, StringComparison.Ordinal)).ToList();
        var comparison = manifest.Compare(ServerActions.FromDescriptors(api));
        var ruled = manifest.Actions.Where(a => a.Rules.Count > 0).ToList();
        var missing = ruled.Where(a => comparison.Missing.Contains(a.Key)).Select(a => a.Key).ToList();
        var changed = comparison.Changed.Where(c => ruled.Any(a => c.StartsWith(a.Key + ":", StringComparison.Ordinal))).ToList();

        var others = descriptors.Except(api).ToList();
        var je = others.Where(ActionRuleTable.IsJellyfinEnhanced).ToList();
        var jeLocked = je.Where(ActionRuleTable.IsJellyfinEnhancedSettingsSave).Select(ActionRuleTable.JellyfinEnhancedName).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var jeUnreviewed = jeLocked.Where(a => !ActionRuleTable.ReviewedJellyfinEnhancedSaves.Contains(a)).ToList();
        var jeUncovered = je
            .Where(d => ActionRuleTable.IsJellyfinEnhancedWrite(d) && IsAuthenticated(d) && !ActionRuleTable.IsJellyfinEnhancedSettingsSave(d))
            .Select(d => (Action: ActionRuleTable.JellyfinEnhancedName(d), Methods: string.Join("|", ActionRuleTable.Methods(d) is { Count: > 0 } m ? m : ["ANY"]), Route: d.AttributeRouteInfo?.Template ?? string.Empty, AdminOnly: RequiresAdministrator(d)))
            .Distinct()
            .OrderBy(u => u.Action, StringComparer.Ordinal)
            .ThenBy(u => u.Route, StringComparer.Ordinal)
            .ThenBy(u => u.Methods, StringComparer.Ordinal)
            .ToList();
        var jeAdminOnly = jeUncovered.Where(u => u.AdminOnly).Select(u => u.Action).Distinct(StringComparer.Ordinal).ToList();
        var flagged = others
            .Select(d => d.ControllerTypeInfo)
            .Distinct()
            .Where(TakesJellyfinController)
            .Select(t => t.FullName ?? t.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        var bound = ruled.Count - missing.Count;
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["CheckedUtc"] = checkedUtc,
            ["FilterRegistered"] = filterRegistered,
            ["EnforcementActive"] = filterRegistered && missing.Count == 0 && changed.Count == 0,
            ["Bound"] = bound,
            ["WithRules"] = ruled.Count,
            ["Missing"] = missing,
            ["Changed"] = changed,
            ["Unreviewed"] = comparison.Unreviewed,
            ["Classification"] = table.Summary(),
            ["JellyfinEnhanced"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["Installed"] = je.Count > 0,
                ["Locked"] = jeLocked,
                ["Unreviewed"] = jeUnreviewed,
                ["Uncovered"] = jeUncovered.Select(u => u.Action + " (" + u.Methods + " " + u.Route + ")").ToList(),
                ["AdminOnly"] = jeAdminOnly,
                ["UncoveredUnreviewed"] = jeUncovered
                    .Where(u => !u.AdminOnly && !ActionRuleTable.ReviewedJellyfinEnhancedUnlocked.Contains(u.Action) && !ActionRuleTable.ReviewedJellyfinEnhancedOutsideSettings.Contains(u.Action))
                    .Select(u => u.Action)
                    .Distinct(StringComparer.Ordinal)
                    .ToList()
            },
            ["PluginActions"] = others
                .GroupBy(d => d.ControllerTypeInfo.Assembly.GetName().Name ?? "(unnamed)", StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Select(d => (d.ControllerTypeInfo.FullName, d.ActionName)).Distinct().Count(), StringComparer.Ordinal),
            ["Flagged"] = flagged
        };
    }

    /// <inheritdoc />
    protected override Task RunAsync(CancellationToken stoppingToken)
    {
        if (_descriptors is null)
        {
            Volatile.Write(ref _result, new Dictionary<string, object?>(StringComparer.Ordinal) { ["Error"] = "MVC's actions aren't available." });
            return Task.CompletedTask;
        }

        var registered = _mvc?.Value.Filters.OfType<ServiceFilterAttribute>().Any(f => f.ServiceType == typeof(AccessRulesActionFilter)) ?? false;
        var result = Check(_table, [.. _descriptors.ActionDescriptors.Items.OfType<ControllerActionDescriptor>()], registered, TimeProvider.GetUtcNow().UtcDateTime);
        Volatile.Write(ref _result, result);
        if (result["EnforcementActive"] is not true)
        {
            Logger.LogWarning(
                "Access Roles' rule table doesn't match this server: filter registered {Registered}, {Missing} actions with rules missing, {Changed} changed; see Diagnostics' RuleTable",
                registered,
                ((IReadOnlyCollection<string>)result["Missing"]!).Count,
                ((IReadOnlyCollection<string>)result["Changed"]!).Count);
        }
        else
        {
            Logger.LogInformation("Access Roles' rule table: {Bound} actions with rules bound, {Unreviewed} unreviewed", result["Bound"], ((IReadOnlyCollection<string>)result["Unreviewed"]!).Count);
        }

        return Task.CompletedTask;
    }

    // An action a caller must be signed in for: no [AllowAnonymous] on it or its controller (MVC's endpoint metadata).
    private static bool IsAuthenticated(ControllerActionDescriptor descriptor)
        => !(descriptor.EndpointMetadata ?? []).OfType<Microsoft.AspNetCore.Authorization.IAllowAnonymous>().Any();

    // A JE action only an administrator gets past: an authorization policy that requires one (on it or its controller, in
    // MVC's endpoint metadata), or JE's own check in the action, reviewed (ActionRuleTable.ReviewedJellyfinEnhancedAdminOnly).
    private static bool RequiresAdministrator(ControllerActionDescriptor descriptor)
        => (descriptor.EndpointMetadata ?? []).OfType<Microsoft.AspNetCore.Authorization.IAuthorizeData>().Any(a => a.Policy is { } policy && AdministratorPolicies.Contains(policy))
            || ActionRuleTable.ReviewedJellyfinEnhancedAdminOnly.Contains(ActionRuleTable.JellyfinEnhancedName(descriptor));

    // A plugin controller that takes one of Jellyfin's controllers and calls it in process (as TrailersController takes
    // ItemsController): a rule keyed on the Jellyfin action never sees those calls.
    private static bool TakesJellyfinController(TypeInfo controller)
        => controller.DeclaredConstructors.Any(c => c.GetParameters().Any(p =>
            (p.ParameterType.FullName ?? string.Empty).StartsWith(JellyfinControllers, StringComparison.Ordinal)
            && p.ParameterType.Name.EndsWith("Controller", StringComparison.Ordinal)));
}
