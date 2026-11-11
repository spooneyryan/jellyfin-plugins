using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.AccessRoles.Rules;

/// <summary>
/// What the global filter does with one action (<see cref="ActionRuleTable.For(Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor)"/>).
/// </summary>
internal sealed record ActionRules
{
    /// <summary>Gets the action's key, <c>&lt;Controller&gt;.&lt;Action&gt;</c>.</summary>
    public required string Key { get; init; }

    /// <summary>Gets every rule of the action, whoever applies it.</summary>
    public IReadOnlyList<ActionManifestRule> All { get; init; } = [];

    /// <summary>Gets the settings rules (<see cref="RuleOwner.Settings"/>), in the manifest's order.</summary>
    public IReadOnlyList<ActionManifestRule> Settings { get; init; } = [];

    /// <summary>
    /// Gets the admin-surface, server-wide and library hardening rules (<see cref="RuleOwner.ServerHardening"/>),
    /// in the manifest's order.
    /// </summary>
    public IReadOnlyList<ActionManifestRule> Hardening { get; init; } = [];

    /// <summary>
    /// Gets the parameters the generic backstop checks: the action's <c>parentId</c>, <c>seriesId</c> and <c>seasonId</c>
    /// arguments (any letter case, by the name MVC binds), read from its descriptor once.
    /// </summary>
    public IReadOnlyList<string> ScopeParameters { get; init; } = [];

    /// <summary>
    /// Gets a value indicating whether the generic backstop was given at run time: the action is outside the manifest and
    /// binds a scope parameter (another plugin's action, or a Jellyfin.Api action a later Jellyfin adds).
    /// </summary>
    public bool RuntimeBackstop { get; init; }

    /// <summary>Gets a value indicating whether a library structure hook applies (an admin's or API key's call).</summary>
    public bool StructureHook { get; init; }

    /// <summary>
    /// Gets the user policy hook that applies: <see cref="RuleKind.UserPolicyPreMerge"/>,
    /// <see cref="RuleKind.PerUserLock"/> or <see cref="RuleKind.SignInHook"/>, or <c>null</c>. The global filter hands it to
    /// the user policy filter, so an action is classified once per request.
    /// </summary>
    public RuleKind? UserPolicyHook { get; init; }

    /// <summary>Gets a value indicating whether the action is Jellyfin.Api's (the manifest's).</summary>
    public bool IsJellyfinApi { get; init; }

    /// <summary>Gets a value indicating whether the action is one of Jellyfin Enhanced's <c>user-settings</c> saves.</summary>
    public bool JellyfinEnhanced { get; init; }

    /// <summary>Gets the action's short name for the audit and the counters: <c>UserController.UpdateUserPassword</c>.</summary>
    public string Endpoint => Key[(Key.LastIndexOf('.', Key.LastIndexOf('.') - 1) + 1)..];

    /// <summary>
    /// Builds an action's rules.
    /// </summary>
    /// <param name="key">The action's key.</param>
    /// <param name="rules">Its rules.</param>
    /// <param name="isJellyfinApi">Whether it is Jellyfin.Api's.</param>
    /// <param name="jellyfinEnhanced">Whether it is a Jellyfin Enhanced settings save.</param>
    /// <param name="scopeParameters">The action's scope parameters (the generic backstop's).</param>
    /// <param name="runtimeBackstop">Whether the backstop was given at run time.</param>
    /// <returns>The rules.</returns>
    public static ActionRules Of(string key, IReadOnlyList<ActionManifestRule> rules, bool isJellyfinApi, bool jellyfinEnhanced = false, IReadOnlyList<string>? scopeParameters = null, bool runtimeBackstop = false) => new()
    {
        Key = key,
        All = rules,
        Settings = [.. rules.Where(r => ActionRuleTable.Owner(r) == RuleOwner.Settings)],
        Hardening = [.. rules.Where(r => ActionRuleTable.Owner(r) == RuleOwner.ServerHardening)],
        ScopeParameters = scopeParameters ?? [],
        RuntimeBackstop = runtimeBackstop,
        StructureHook = rules.Any(r => r.Kind == RuleKind.LibraryStructureHook),
        UserPolicyHook = UserPolicyHookOf(rules),
        IsJellyfinApi = isJellyfinApi,
        JellyfinEnhanced = jellyfinEnhanced
    };

    /// <summary>
    /// Gets the user policy hook of an action's rules: the pre-merge before the lock before the sign-in hook.
    /// </summary>
    /// <param name="rules">The action's rules.</param>
    /// <returns>The hook, or <c>null</c>.</returns>
    public static RuleKind? UserPolicyHookOf(IEnumerable<ActionManifestRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var kinds = rules.Select(r => r.Kind).ToHashSet();
        foreach (var kind in new[] { RuleKind.UserPolicyPreMerge, RuleKind.PerUserLock, RuleKind.SignInHook })
        {
            if (kinds.Contains(kind))
            {
                return kind;
            }
        }

        return null;
    }
}
