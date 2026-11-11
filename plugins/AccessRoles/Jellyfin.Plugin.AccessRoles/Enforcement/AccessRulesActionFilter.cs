using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Jellyfin.Plugin.AccessRoles.Configuration;
using Jellyfin.Plugin.AccessRoles.Model;
using Jellyfin.Plugin.AccessRoles.Policy;
using Jellyfin.Plugin.AccessRoles.Rules;
using Jellyfin.Plugin.AccessRoles.Storage;
using Jellyfin.Plugin.AccessRoles.Sync;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Storage;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AccessRoles.Enforcement;

/// <summary>
/// The <c>AccessRulesActionFilter</c>: Access Roles' one global MVC action filter, at order −10000, so
/// it is the first action filter of every action, before <c>UnsupportedContentTypeFilter</c> (−3000), the
/// <c>[ApiController]</c> model-validation filter (−2000) and Jellyfin Enhanced's filters (0) (verified on 10.11.5).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>What applies</b> comes from the <see cref="ActionRuleTable"/> (the manifest, by controller and action name, plus
/// Jellyfin Enhanced's <c>user-settings</c> saves); an action without a rule costs one cached lookup.</item>
/// <item><b>Order of checks</b>: an API key or the <c>Administrator</c> role claim first; for those callers only
/// the admin-touching rows run, and no lock ever applies to them (administrators are never restricted). An anonymous caller meets no settings rule (the
/// settings actions all need a signed-in user, and the server-wide rows that cover anonymous callers are the hardening's).</item>
/// <item><b>Settings rules</b> (<see cref="RuleOwner.Settings"/>), for a signed-in non-admin whose locks the
/// <see cref="EffectiveRulesCache"/> gives: the Profile rejects (password, user image) and the Quick Connect and Jellyfin
/// Enhanced rejects answer 403 <c>urn:jellyfin-plugin-kit:locked</c> (the kit's ProblemDetails, its
/// <c>Locked</c> kind, never <c>ParentalControl</c>); <c>UpdateUser</c> refuses a name change (ordinal, as the core compares)
/// under the Profile lock and otherwise merges, like <c>UpdateUserConfiguration</c>, the locked fields' stored values
/// (<see cref="UserConfigurationMerger"/>); a <c>usersettings</c> save merges the locked keys
/// (<see cref="DisplayPreferencesMerger"/>). Reject rules apply whatever the body; a merge passes through when the model
/// state is invalid or its argument is missing or null, so the core answers its own 400. Every refusal, and every
/// merge that put a locked value back, is counted (<see cref="BlockCounter"/>, audited per 10-minute window).</item>
/// <item><b>The hooks</b> (<see cref="RuleOwner.LibraryHooks"/>): the library structure filter and user policy filter
/// (the per-user lock, the <c>UpdateUserPolicy</c> pre-merge, the sign-in hook, a plugin's <c>AuthenticationResult</c>)
/// run as they did when they were filters of their own, inside this one.</item>
/// <item><b>The hardening rules</b> (<see cref="RuleOwner.ServerHardening"/>, since 0.9.7) go to <see cref="ServerHardening"/> for every
/// caller that is neither an administrator nor an API key, after the settings rules: the <c>AdminSurfaces</c> and
/// <c>UserDirectory</c> locks, the configuration reads, the restart block and the public user list (anonymous callers too
/// for those two), and the library leak hardening (every signed-in non-admin). Each one's own time per request is recorded
/// (<see cref="HardeningCost"/>). The <c>Strict</c> rules are classified and not applied.</item>
/// <item><b>Failure modes</b>: a settings rule or (since 0.9.7) an admin-surface, server-wide or hardening rule that
/// throws, before the action or on its result, or a lock snapshot that can't be built (the store unreadable), refuses that
/// request with 403 <c>Locked</c> "temporarily unavailable" (fail closed), counted; a store that
/// can't be trusted locks every server-enforced section for every non-admin (<see cref="LockSnapshot"/>). An action whose
/// rules can't be read (the rule table's lookup throws) is refused the same way for a signed-in non-admin, since its rules
/// might be settings rules, while an administrator's, an API key's and an anonymous caller's request
/// passes, with the hooks found by their own lookups. The hooks keep their own failure modes (the pre-merge fails open).
/// With <c>EnforcementEnabled</c> off or the SAFE-MODE file present, no settings rule and no hardening rule applies.</item>
/// <item><b>One lookup per request</b>: the rule table classifies the action once per descriptor,
/// and the user policy hook it found goes to the user policy filter with the request.</item>
/// </list>
/// </remarks>
internal sealed class AccessRulesActionFilter : IAsyncActionFilter, IOrderedFilter
{
    /// <summary>The filter's order among MVC's filters (verified on 10.11.5).</summary>
    public const int FilterOrder = -10000;

    /// <summary>The <c>UserConfiguration</c> argument of <c>UpdateUserConfiguration</c> and its legacy route.</summary>
    public const string UserConfigArgument = "userConfig";

    /// <summary>The <c>UserDto</c> argument of <c>UpdateUser</c> and its legacy route.</summary>
    public const string UpdateUserArgument = "updateUser";

    /// <summary>The target user's argument (optional on the modern routes: the caller then).</summary>
    public const string UserIdArgument = "userId";

    /// <summary>The <c>DisplayPreferencesDto</c> argument of <c>UpdateDisplayPreferences</c>.</summary>
    public const string DisplayPreferencesArgument = "displayPreferences";

    /// <summary>The document id argument of <c>UpdateDisplayPreferences</c>.</summary>
    public const string DisplayPreferencesIdArgument = "displayPreferencesId";

    /// <summary>The client argument of <c>UpdateDisplayPreferences</c>.</summary>
    public const string ClientArgument = "client";

    /// <summary>The detail of a request refused because a rule couldn't decide ("temporarily unavailable", fail closed).</summary>
    public const string UnavailableMessage = "Settings changes are temporarily unavailable: Access Roles couldn't check this one, so it was refused. Try again in a moment.";

    /// <summary>The rule name of a refusal whose section a failing rule couldn't tell.</summary>
    public const string UnknownRule = "Settings";

    /// <summary>The detail of a refused name change under the Profile lock.</summary>
    public const string NameLockedMessage = "The Profile settings are managed by your server administrator, so the user name can't be changed.";

    /// <summary>
    /// The response header of a merged save that put locked values back: the sections, comma-separated. jellyfin-web
    /// shows its own "saved" for the 204, so the web guard tells the user from it; the caller already knows their locks
    /// (<c>/Me</c>), so it says nothing new. It is set only once the action has run and succeeded (no
    /// exception, a 2xx result), so a save that failed never names sections as kept.
    /// </summary>
    public const string KeptHeader = "X-AccessRoles-Kept";

    private readonly ActionRuleTable _table;
    private readonly EffectiveRulesCache _cache;
    private readonly IPluginConfigurationProvider _configuration;
    private readonly AccessRolesPaths _paths;
    private readonly IUserManager _userManager;
    private readonly IDisplayPreferencesManager? _displayPreferences;
    private readonly BlockCounter _counter;
    private readonly EnforcementStatus _status;
    private readonly UserPolicyFilter _userPolicy;
    private readonly LibraryStructureFilter _structure;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly WebClientDefaults _webDefaults;
    private readonly ServerHardening? _hardening;
    private readonly HardeningCost _cost;

    /// <summary>
    /// Initializes a new instance of the <see cref="AccessRulesActionFilter"/> class.
    /// </summary>
    /// <param name="table">The rule table.</param>
    /// <param name="cache">The lock snapshots.</param>
    /// <param name="configuration">The settings (<c>EnforcementEnabled</c>).</param>
    /// <param name="paths">The plugin's paths (the SAFE-MODE file).</param>
    /// <param name="userManager">The user manager, for the stored configuration and name.</param>
    /// <param name="displayPreferences">The display-preferences manager, for the stored <c>usersettings</c> document.</param>
    /// <param name="counter">The blocked-request counter.</param>
    /// <param name="status">What Diagnostics shows.</param>
    /// <param name="userPolicy">The user policy hooks.</param>
    /// <param name="structure">The library structure hook.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="timeProvider">The clock; the system clock by default.</param>
    /// <param name="webDefaults">What jellyfin-web takes for a display-preference key with no value; the stock web client's by default.</param>
    /// <param name="hardening">The hardening rules; without them (a test of the settings rules alone) an action with a hardening rule is refused for a
    /// non-admin, since its rules can't be applied (fail closed).</param>
    /// <param name="cost">The hardening rules' times.</param>
    public AccessRulesActionFilter(
        ActionRuleTable table,
        EffectiveRulesCache cache,
        IPluginConfigurationProvider configuration,
        AccessRolesPaths paths,
        IUserManager userManager,
        IDisplayPreferencesManager? displayPreferences,
        BlockCounter counter,
        EnforcementStatus status,
        UserPolicyFilter userPolicy,
        LibraryStructureFilter structure,
        ILogger logger,
        TimeProvider? timeProvider = null,
        WebClientDefaults? webDefaults = null,
        ServerHardening? hardening = null,
        HardeningCost? cost = null)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(userManager);
        ArgumentNullException.ThrowIfNull(counter);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(userPolicy);
        ArgumentNullException.ThrowIfNull(structure);
        ArgumentNullException.ThrowIfNull(logger);
        _table = table;
        _cache = cache;
        _configuration = configuration;
        _paths = paths;
        _userManager = userManager;
        _displayPreferences = displayPreferences;
        _counter = counter;
        _status = status;
        _userPolicy = userPolicy;
        _structure = structure;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _webDefaults = webDefaults ?? WebClientDefaults.Stock;
        _hardening = hardening;
        _cost = cost ?? new HardeningCost();
    }

    /// <inheritdoc />
    public int Order => FilterOrder;

    /// <summary>
    /// Gets the words a refusal uses for a section (the <c>detail</c> names the section).
    /// </summary>
    /// <param name="section">The section.</param>
    /// <returns>Its title.</returns>
    public static string Title(AccessRolesLock section) => section switch
    {
        AccessRolesLock.DisplayAccount => "Display (account)",
        AccessRolesLock.DisplayDevice => "Display (this device)",
        AccessRolesLock.Home => "Home screen",
        AccessRolesLock.QuickConnect => "Quick Connect",
        AccessRolesLock.EnhancedPanel => "Jellyfin Enhanced",
        AccessRolesLock.AdminSurfaces => "Dashboard",
        AccessRolesLock.UserDirectory => "User list",
        _ => section.ToString()
    };

    /// <summary>
    /// Gets the detail of a refusal under a section's lock.
    /// </summary>
    /// <param name="section">The section.</param>
    /// <returns>The detail.</returns>
    public static string LockedMessage(AccessRolesLock section)
        => $"The {Title(section)} settings are managed by your server administrator, so this change isn't allowed.";

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        if (context.ActionDescriptor is not ControllerActionDescriptor descriptor)
        {
            await next().ConfigureAwait(false);
            return;
        }

        ActionRules rules;
        try
        {
            rules = _table.For(descriptor);
        }
        catch (Exception ex)
        {
            await UnreadableRulesAsync(context, next, descriptor, ex).ConfigureAwait(false);
            return;
        }

        if (rules.StructureHook)
        {
            await _structure.OnActionExecutionAsync(context, next).ConfigureAwait(false);
            return;
        }

        var kept = new SortedSet<AccessRolesLock>();
        if (rules.Settings.Count > 0 && Refused(context, rules, kept))
        {
            return;
        }

        var inner = next;
        if (rules.Hardening.Count > 0)
        {
            var started = Stopwatch.GetTimestamp();
            var answered = Hardened(context, rules, out var after, out var applied);
            var before = Stopwatch.GetElapsedTime(started);
            if (applied is not null && (answered || after is null))
            {
                _cost.Record(applied, before);
            }

            if (answered)
            {
                return;
            }

            if (after is not null && applied is not null)
            {
                inner = Rewriting(context, rules, inner, after, applied, before);
            }
        }

        if (kept.Count > 0)
        {
            inner = TellingKept(context, inner, kept);
        }

        await _userPolicy.RunAsync(context, inner, rules.UserPolicyHook).ConfigureAwait(false);
    }

    // The hardening rules before the action, for a caller that is neither an administrator nor an API key: true when the
    // request was answered or refused here; the step that rewrites the action's result comes back in after, and applied
    // names the rules that ran (ServerHardening.RuleNames; null when none applied). A rule that throws refuses the request
    // (fail closed).
    private bool Hardened(ActionExecutingContext context, ActionRules rules, out Action<ActionExecutedContext>? after, out string? applied)
    {
        after = null;
        applied = null;
        var caller = context.HttpContext.User;
        if (IsAdministratorOrApiKey(caller))
        {
            return false;
        }

        var userId = caller.GetUserId();
        HardeningSettings? settings = null;
        try
        {
            if (!EnforcementOn())
            {
                return false;
            }

            settings = ServerHardening.Settings(_configuration.GetConfiguration());
            if (!ServerHardening.Applies(rules, userId, settings))
            {
                return false;
            }

            applied = ServerHardening.RuleNames(rules, userId, settings);
            var hardening = _hardening ?? throw new InvalidOperationException("Access Roles' admin-surface and hardening rules aren't available.");
            after = hardening.Before(context, rules, userId, () => _cache.Current().For(userId), settings);
            return context.Result is not null;
        }
        catch (Exception ex)
        {
            after = null;
            applied ??= ServerHardening.RuleNames(rules, userId, settings);
            context.Result = HardeningFailed(rules, applied, userId, ex);
            return true;
        }
    }

    // The rewrite of the action's result, only for an action that ran and succeeded; a rewrite that throws refuses the
    // answer (fail closed). The rules' time is before plus after, under the names of the rules that ran.
    private ActionExecutionDelegate Rewriting(ActionExecutingContext context, ActionRules rules, ActionExecutionDelegate next, Action<ActionExecutedContext> after, string applied, TimeSpan before)
        => async () =>
        {
            var executed = await next().ConfigureAwait(false);
            var started = Stopwatch.GetTimestamp();
            try
            {
                if (Succeeded(executed) && executed.Result is not null)
                {
                    after(executed);
                }
            }
            catch (Exception ex)
            {
                executed.Result = HardeningFailed(rules, applied, context.HttpContext.User.GetUserId(), ex);
            }
            finally
            {
                _cost.Record(applied, before + Stopwatch.GetElapsedTime(started));
            }

            return executed;
        };

    private Microsoft.AspNetCore.Mvc.IActionResult HardeningFailed(ActionRules rules, string ruleNames, Guid userId, Exception exception)
    {
        _status.Error(rules.Endpoint + ": " + exception.GetType().Name, _timeProvider.GetUtcNow().UtcDateTime);
        _counter.Record(userId, ruleNames, rules.Endpoint, BlockCounter.FailedClosed);
        _logger.LogError(exception, "An Access Roles rule of {Endpoint} failed for {Caller}; the request is refused (fail closed)", rules.Endpoint, userId.Equals(Guid.Empty) ? "an anonymous caller" : userId.ToString("N"));
        return KitProblemDetails.Result(PluginErrorKind.Locked, ServerHardening.UnavailableMessage);
    }

    // A merge that put locked values back says so (KeptHeader), but only for a save that went through: the header is set
    // after the action ran, before its result is written, when it threw nothing and answered 2xx.
    private static ActionExecutionDelegate TellingKept(ActionExecutingContext context, ActionExecutionDelegate next, IReadOnlyCollection<AccessRolesLock> kept)
        => async () =>
        {
            var executed = await next().ConfigureAwait(false);
            if (Succeeded(executed))
            {
                context.HttpContext.Response.Headers[KeptHeader] = string.Join(",", kept.Select(k => k.ToString()));
            }

            return executed;
        };

    private static bool Succeeded(ActionExecutedContext executed)
        => (executed.Exception is null || executed.ExceptionHandled)
            && executed.Result is not IStatusCodeActionResult { StatusCode: < 200 or >= 300 };

    private static bool IsAdministratorOrApiKey(ClaimsPrincipal caller) => caller.IsApiKey() || caller.IsInRole(UserPolicyFilter.AdministratorRole);

    // The short name of an action whose rules couldn't be read, as ActionRules.Endpoint gives it: UserController.UpdateUser.
    private static string EndpointOf(ControllerActionDescriptor descriptor)
    {
        try
        {
            return descriptor.ControllerTypeInfo.Name + "." + descriptor.ActionName;
        }
        catch (Exception)
        {
            return descriptor.ControllerName + "Controller." + descriptor.ActionName;
        }
    }

    // The rule table's lookup threw: the action may have a settings rule, so a signed-in non-admin's
    // request is refused (fail closed, counted as FilterErrors); an administrator's, an API key's and an anonymous
    // caller's pass, and the hooks run for them, found by their own lookups (never a 500 for everyone).
    private async Task UnreadableRulesAsync(ActionExecutingContext context, ActionExecutionDelegate next, ControllerActionDescriptor descriptor, Exception exception)
    {
        var caller = context.HttpContext.User;
        var userId = caller.GetUserId();
        if (!IsAdministratorOrApiKey(caller) && !userId.Equals(Guid.Empty) && EnforcementOn())
        {
            var endpoint = EndpointOf(descriptor);
            _status.Error(endpoint + ": " + exception.GetType().Name, _timeProvider.GetUtcNow().UtcDateTime);
            _counter.Record(userId, UnknownRule, endpoint, BlockCounter.FailedClosed);
            _logger.LogError(exception, "Access Roles couldn't read the rules of {Endpoint}; user {UserId}'s request is refused (fail closed)", endpoint, userId);
            context.Result = KitProblemDetails.Result(PluginErrorKind.Locked, UnavailableMessage);
            return;
        }

        _logger.LogError(exception, "Access Roles couldn't read the rules of {Action}; the request goes on with the hooks only", descriptor.ActionName);
        bool structure;
        try
        {
            structure = _structure.Watches(descriptor);
        }
        catch (Exception)
        {
            structure = false;
        }

        if (structure)
        {
            await _structure.OnActionExecutionAsync(context, next).ConfigureAwait(false);
            return;
        }

        await _userPolicy.OnActionExecutionAsync(context, next).ConfigureAwait(false);
    }

    // Whether the settings rules apply at all: EnforcementEnabled on and no SAFE-MODE file. A setting that can't be read
    // counts as on (fail closed).
    private bool EnforcementOn()
    {
        try
        {
            return _configuration.GetConfiguration().EnforcementEnabled && !File.Exists(_paths.SafeModeFile);
        }
        catch (Exception)
        {
            return true;
        }
    }

    // The target of a user action: its userId argument (a Guid, or a route string another plugin binds), else the caller.
    private static Guid TargetOf(ActionExecutingContext context, Guid caller)
    {
        if (!context.ActionArguments.TryGetValue(UserIdArgument, out var value) || value is null)
        {
            return caller;
        }

        return value switch
        {
            Guid id when !id.Equals(Guid.Empty) => id,
            string text when Guid.TryParse(text, out var parsed) && !parsed.Equals(Guid.Empty) => parsed,
            _ => caller
        };
    }

    // Whether a non-admin's request is refused by a settings rule; merges happen in place, and the sections whose locked
    // values they put back are added to kept. Fail closed.
    private bool Refused(ActionExecutingContext context, ActionRules rules, ISet<AccessRolesLock> kept)
    {
        var caller = context.HttpContext.User;
        if (IsAdministratorOrApiKey(caller))
        {
            return false;
        }

        var userId = caller.GetUserId();
        if (userId.Equals(Guid.Empty))
        {
            return false;
        }

        try
        {
            var configuration = _configuration.GetConfiguration();
            if (!configuration.EnforcementEnabled || File.Exists(_paths.SafeModeFile))
            {
                return false;
            }

            var snapshot = _cache.Current();
            if (_status.Degraded(snapshot.DegradedBecause) && snapshot.Degraded is { } degraded)
            {
                _logger.LogWarning(
                    "Access Roles is in Degraded mode ({Status}): every server-enforced settings section is locked for {Who} until an admin puts it right (fail closed)",
                    degraded.Because,
                    degraded.EveryNonAdmin ? "every non-admin" : degraded.Users.Count + " managed users");
            }

            var locked = snapshot.For(userId);
            foreach (var rule in rules.Settings)
            {
                if (Apply(context, rules, rule, userId, locked, snapshot, kept))
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            _status.Error(rules.Endpoint + ": " + ex.GetType().Name, _timeProvider.GetUtcNow().UtcDateTime);
            _counter.Record(userId, UnknownRule, rules.Endpoint, BlockCounter.FailedClosed);
            _logger.LogError(ex, "An Access Roles settings rule of {Endpoint} failed for user {UserId}; the request is refused (fail closed)", rules.Endpoint, userId);
            context.Result = KitProblemDetails.Result(PluginErrorKind.Locked, UnavailableMessage);
            return true;
        }
    }

    private bool Apply(ActionExecutingContext context, ActionRules rules, ActionManifestRule rule, Guid userId, IReadOnlySet<AccessRolesLock> locked, LockSnapshot snapshot, ISet<AccessRolesLock> kept)
    {
        switch (rule.Kind)
        {
            case RuleKind.LockedReject when rule.Lock is { } section:
                // Jellyfin Enhanced writes the settings of the user its route names: that user's lock counts too.
                var target = rules.JellyfinEnhanced ? TargetOf(context, userId) : userId;
                if (locked.Contains(section) || (!target.Equals(userId) && snapshot.For(target).Contains(section)))
                {
                    return Refuse(context, rules, userId, section, LockedMessage(section));
                }

                return false;
            case RuleKind.UserUpdateMerge:
                return MergeUpdateUser(context, rules, userId, locked, kept);
            case RuleKind.UserConfigurationMerge:
                MergeUserConfiguration(context, rules, userId, locked, kept);
                return false;
            case RuleKind.DisplayPreferencesMerge:
                MergeDisplayPreferences(context, rules, userId, locked, kept);
                return false;
            default:
                throw new InvalidOperationException($"The settings filter has no rule {rule}.");
        }
    }

    private bool Refuse(ActionExecutingContext context, ActionRules rules, Guid userId, AccessRolesLock section, string detail)
    {
        _counter.Record(userId, SettingsSections.RuleName(section), rules.Endpoint, BlockCounter.Refused);
        _logger.LogDebug("Access Roles refused {Endpoint} for user {UserId}: {Lock} is locked", rules.Endpoint, userId, section);
        context.Result = KitProblemDetails.Result(PluginErrorKind.Locked, detail);
        return true;
    }

    private void Counted(ActionRules rules, Guid userId, IReadOnlyDictionary<AccessRolesLock, IReadOnlyList<string>> reverted, ISet<AccessRolesLock> kept)
    {
        foreach (var section in reverted.Keys)
        {
            _counter.Record(userId, SettingsSections.RuleName(section), rules.Endpoint, BlockCounter.Reverted);
            kept.Add(section);
        }

        if (reverted.Count > 0)
        {
            _logger.LogDebug("Access Roles kept {Count} locked fields of user {UserId} on {Endpoint}", reverted.Values.Sum(f => f.Count), userId, rules.Endpoint);
        }
    }

    // UpdateUser (POST /Users, and its legacy route): a name change under the Profile lock is refused; the posted
    // configuration is merged. A body that failed binding or validation, or another user's update (the core refuses a
    // non-admin's), passes on.
    private bool MergeUpdateUser(ActionExecutingContext context, ActionRules rules, Guid userId, IReadOnlySet<AccessRolesLock> locked, ISet<AccessRolesLock> kept)
    {
        if (locked.Count == 0
            || !context.ModelState.IsValid
            || !context.ActionArguments.TryGetValue(UpdateUserArgument, out var value)
            || value is not UserDto posted
            || !TargetOf(context, userId).Equals(userId)
            || _userManager.GetUserById(userId) is not { } user)
        {
            return false;
        }

        // The core renames when the names differ by StringComparison.Ordinal (UserController.cs:393): a change of letter
        // case is a rename too.
        if (locked.Contains(AccessRolesLock.Profile) && !string.Equals(user.Username, posted.Name, StringComparison.Ordinal))
        {
            return Refuse(context, rules, userId, AccessRolesLock.Profile, NameLockedMessage);
        }

        if (posted.Configuration is { } configuration && UserConfigurationMerger.Applies(locked))
        {
            var stored = _userManager.GetUserDto(user).Configuration ?? new UserConfiguration();
            Counted(rules, userId, UserConfigurationMerger.Merge(configuration, stored, locked), kept);
        }

        return false;
    }

    private void MergeUserConfiguration(ActionExecutingContext context, ActionRules rules, Guid userId, IReadOnlySet<AccessRolesLock> locked, ISet<AccessRolesLock> kept)
    {
        if (!UserConfigurationMerger.Applies(locked)
            || !context.ModelState.IsValid
            || !context.ActionArguments.TryGetValue(UserConfigArgument, out var value)
            || value is not UserConfiguration posted
            || !TargetOf(context, userId).Equals(userId)
            || _userManager.GetUserById(userId) is not { } user)
        {
            return;
        }

        var stored = _userManager.GetUserDto(user).Configuration ?? new UserConfiguration();
        Counted(rules, userId, UserConfigurationMerger.Merge(posted, stored, locked), kept);
    }

    private void MergeDisplayPreferences(ActionExecutingContext context, ActionRules rules, Guid userId, IReadOnlySet<AccessRolesLock> locked, ISet<AccessRolesLock> kept)
    {
        if (!DisplayPreferencesMerger.Applies(locked)
            || !context.ModelState.IsValid
            || !context.ActionArguments.TryGetValue(DisplayPreferencesArgument, out var value)
            || value is not DisplayPreferencesDto posted
            || !context.ActionArguments.TryGetValue(DisplayPreferencesIdArgument, out var idValue)
            || idValue is not string documentText
            || !context.ActionArguments.TryGetValue(ClientArgument, out var clientValue)
            || clientValue is not string client
            || !DisplayPreferencesMerger.IsUserSettings(documentText, client)
            || !TargetOf(context, userId).Equals(userId))
        {
            return;
        }

        var manager = _displayPreferences ?? throw new InvalidOperationException("The server has no display-preferences manager.");
        var documentId = DisplayPreferencesMerger.DocumentId(documentText);
        var stored = manager.GetDisplayPreferences(userId, documentId, client);
        var storedCustom = manager.ListCustomItemDisplayPreferences(userId, documentId, client);
        Counted(rules, userId, DisplayPreferencesMerger.Merge(posted, stored, storedCustom, locked, _webDefaults.Defaults), kept);
    }
}
