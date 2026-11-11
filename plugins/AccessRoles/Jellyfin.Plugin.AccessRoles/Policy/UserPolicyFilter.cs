using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AccessRoles.Access;
using Jellyfin.Plugin.AccessRoles.Audit;
using Jellyfin.Plugin.AccessRoles.Rules;
using Jellyfin.Plugin.AccessRoles.Sync;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Users;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Users;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AccessRoles.Policy;

/// <summary>
/// The user policy hooks: an action filter that watches the
/// <c>UserController</c> actions the manifest marks <c>UserPolicyPreMerge</c>, <c>PerUserLock</c> and <c>SignInHook</c>, and
/// any plugin action that answers an <c>AuthenticationResult</c> (an SSO plugin's sign-in). Every
/// other action is one dictionary lookup and one type check of its result.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b><c>UpdateUserPolicy</c></b> (an admin's or an API key's <c>POST /Users/{id}/Policy</c>; the only callers
/// <c>RequiresElevation</c> admits): takes the user's policy lock, which <see cref="PolicyWriter"/> holds from its first read
/// to <c>UpdatePolicyAsync</c>, so a Dashboard save and a write of ours never interleave (otherwise a
/// commit between our re-read and the core's load inside <c>UpdatePolicyAsync</c> was written over); then pre-merges the
/// library fields the next sync run would write (<see cref="LibrarySync.DecidePreMerge"/>), so the save carries them: one
/// write. Only <c>EnableAllFolders</c> and <c>EnabledFolders</c> of the post are ever changed: its
/// <c>IsAdministrator</c>, <c>IsDisabled</c> and every other field are the admin's. After a 2xx the save is recorded and
/// audited (<see cref="LibrarySync.RecordPreMergeAsync"/>). A body that failed binding or validation passes through untouched
/// (the core answers its own 400). Fail open: the lock not free in <see cref="LockTimeout"/> lets the post through without it,
/// still pre-merged; a pre-merge that throws lets it through unchanged, audited <c>DriftDetected</c> (<c>HookFailed</c>),
/// and the next run applies the rules.</item>
/// <item><b><c>UpdateUser</c>, <c>UpdateUserLegacy</c>, <c>DeleteUser</c></b> by an administrator or API key: the same lock
/// around the action, nothing changed.</item>
/// <item><b>Sign-ins</b> (<c>AuthenticateUserByName</c>, the obsolete <c>AuthenticateUser</c> under its own name, Quick
/// Connect, and a plugin's action answering an <c>AuthenticationResult</c>): after the action, a managed user's library
/// fields are corrected through the sync's one-user run before the response leaves (<see cref="LibrarySync.CorrectUserAsync"/>:
/// the user's policy lock only, at most <see cref="SignInWait"/>), and whenever the server enforces other library fields than
/// <c>AuthenticationResult.User</c> carries (the hook's write, or a run's while the hook waited), <c>User</c> is replaced with
/// a fresh <c>GetUserDto</c> (on 10.11.5 the response otherwise carries the stale policy); runs follow
/// <see cref="SignInFollowUps"/> later (the backstop for a second device signing in at that moment).
/// Fail closed: when the correction failed (the user's lock busy, the user still being created by another request, the
/// write failed, the hook threw) or the store holds the sync (the restore hold, a read-only store, Degraded mode: compared
/// with the last write) while the server enforces more, a non-administrator's sign-in is refused with 503 and
/// <c>Retry-After</c>, and the token the core issued is revoked (audited <c>SignInRefused</c> with whether it was); an
/// administrator's never is, nor anyone's while an admin paused the sync (library access untouched).</item>
/// </list>
/// <para>Since 0.9.5 it is no global filter of its own: Access Roles' one global filter, <c>AccessRulesActionFilter</c> at
/// −10000, runs it for every action after its settings rules, so it behaves as it did at the same order.</para>
/// </remarks>
internal sealed class UserPolicyFilter : IAsyncActionFilter
{
    /// <summary>The order it runs at: the global filter's −10000, inside <c>AccessRulesActionFilter</c> since 0.9.5.</summary>
    public const int Order = -10000;

    /// <summary>The role claim of administrators (Jellyfin's <c>UserRoles.Administrator</c>).</summary>
    public const string AdministratorRole = "Administrator";

    /// <summary>The detail of a refused sign-in.</summary>
    public const string SignInRefusedMessage = "Access Roles couldn't apply this account's library access just now, so the sign-in was refused. Sign in again in a moment; if it keeps happening, an administrator can see why in Access Roles' audit log.";

    /// <summary>The refused sign-in's <c>Retry-After</c> when a correction failed (seconds).</summary>
    public const int RetryAfterSeconds = 5;

    /// <summary>The refused sign-in's <c>Retry-After</c> while the store holds the sync (seconds: an admin has to confirm).</summary>
    public const int HeldRetryAfterSeconds = 60;

    private const string JellyfinApi = "Jellyfin.Api";

    /// <summary>How long an admin's save waits for the user's policy lock before it goes on without it (fail open).</summary>
    public static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(5);

    /// <summary>How long a sign-in's correction waits for the user's policy lock before it counts as failed.</summary>
    public static readonly TimeSpan SignInWait = TimeSpan.FromSeconds(5);

    /// <summary>The runs after a managed user's sign-in (+5 s and +30 s).</summary>
    public static readonly IReadOnlyList<TimeSpan> SignInFollowUps = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30)];

    private readonly IReadOnlyDictionary<(string Controller, string Action), RuleKind> _actions;
    private readonly UserPolicyLocks _locks;
    private readonly LibrarySync _sync;
    private readonly LibrarySyncService _service;
    private readonly IServerUsers _users;
    private readonly IUserManager _userManager;
    private readonly ISessionManager? _sessions;
    private readonly LibrarySyncStatus _status;
    private readonly ILogger _logger;
    private readonly TimeSpan _lockTimeout;
    private readonly TimeSpan _signInWait;

    /// <summary>
    /// Initializes a new instance of the <see cref="UserPolicyFilter"/> class.
    /// </summary>
    /// <param name="manifest">The action manifest: the actions with the three rules.</param>
    /// <param name="locks">The per-user policy locks, shared with the writer.</param>
    /// <param name="sync">The library sync.</param>
    /// <param name="service">The sync's scheduler, for the runs after a sign-in or a failed save.</param>
    /// <param name="users">The server's users, for the audit actor's name.</param>
    /// <param name="userManager">The user manager, for the sign-in response's fresh DTO.</param>
    /// <param name="sessions">The session manager, to revoke a refused sign-in's token; <c>null</c> in tests.</param>
    /// <param name="status">What Diagnostics shows.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="lockTimeout">How long a save waits for the lock; <see cref="LockTimeout"/> by default.</param>
    /// <param name="signInWait">How long a sign-in's correction waits for the user's lock; <see cref="SignInWait"/> by default.</param>
    public UserPolicyFilter(
        ActionManifest manifest,
        UserPolicyLocks locks,
        LibrarySync sync,
        LibrarySyncService service,
        IServerUsers users,
        IUserManager userManager,
        ISessionManager? sessions,
        LibrarySyncStatus status,
        ILogger logger,
        TimeSpan? lockTimeout = null,
        TimeSpan? signInWait = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(locks);
        ArgumentNullException.ThrowIfNull(sync);
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(userManager);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(logger);
        _actions = Actions(manifest);
        _locks = locks;
        _sync = sync;
        _service = service;
        _users = users;
        _userManager = userManager;
        _sessions = sessions;
        _status = status;
        _logger = logger;
        _lockTimeout = lockTimeout ?? LockTimeout;
        _signInWait = signInWait ?? SignInWait;
    }

    /// <summary>Gets the watched actions by controller and name, with the rule each gets.</summary>
    public IReadOnlyDictionary<(string Controller, string Action), RuleKind> WatchedActions => _actions;

    /// <summary>
    /// Finds the actions a manifest marks with <c>UserPolicyPreMerge</c>, <c>PerUserLock</c> or <c>SignInHook</c>.
    /// </summary>
    /// <param name="manifest">The manifest.</param>
    /// <returns>The actions, each with its rule (the pre-merge before the lock before the sign-in hook).</returns>
    public static IReadOnlyDictionary<(string Controller, string Action), RuleKind> Actions(ActionManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var result = new Dictionary<(string Controller, string Action), RuleKind>();
        foreach (var action in manifest.Actions)
        {
            if (ActionRules.UserPolicyHookOf(action.Rules) is { } kind)
            {
                result.TryAdd((action.Controller, action.Action), kind);
            }
        }

        return result;
    }

    /// <summary>
    /// Gets the hook of an action, found by its controller's full name and its action name, as the manifest names them.
    /// </summary>
    /// <param name="descriptor">The action.</param>
    /// <returns>The hook, or <c>null</c>.</returns>
    public RuleKind? HookOf(ControllerActionDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return _actions.TryGetValue((descriptor.ControllerTypeInfo.FullName ?? string.Empty, descriptor.ActionName), out var kind) ? kind : null;
    }

    /// <inheritdoc />
    public Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        return RunAsync(context, next, context.ActionDescriptor is ControllerActionDescriptor descriptor ? HookOf(descriptor) : null);
    }

    /// <summary>
    /// Runs the hooks for an action the caller already classified (the global filter's <see cref="ActionRules.UserPolicyHook"/>,
    /// so an action costs one lookup per request).
    /// </summary>
    /// <param name="context">The action's context.</param>
    /// <param name="next">The rest of the pipeline.</param>
    /// <param name="hook">The action's hook, or <c>null</c>: only a plugin's <c>AuthenticationResult</c> is then looked at.</param>
    /// <returns>A task that completes with the action.</returns>
    public async Task RunAsync(ActionExecutingContext context, ActionExecutionDelegate next, RuleKind? hook)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        if (context.ActionDescriptor is not ControllerActionDescriptor descriptor)
        {
            await next().ConfigureAwait(false);
            return;
        }

        if (hook is { } kind)
        {
            switch (kind)
            {
                case RuleKind.UserPolicyPreMerge:
                    await PreMergeAsync(context, next).ConfigureAwait(false);
                    return;
                case RuleKind.PerUserLock:
                    await LockAsync(context, next).ConfigureAwait(false);
                    return;
                default:
                    await SignedInAsync(context, await next().ConfigureAwait(false)).ConfigureAwait(false);
                    return;
            }
        }

        var executed = await next().ConfigureAwait(false);

        // A plugin's own sign-in (an SSO plugin's controller) is covered when it answers an AuthenticationResult through MVC
        // (UNVERIFIED, no such plugin was at hand). Jellyfin's own actions are the manifest's.
        if (executed.Result is ObjectResult { Value: AuthenticationResult }
            && !string.Equals(descriptor.ControllerTypeInfo.Assembly.GetName().Name, JellyfinApi, StringComparison.Ordinal))
        {
            await SignedInAsync(context, executed).ConfigureAwait(false);
        }
    }

    private static bool Succeeded(ActionExecutedContext executed)
    {
        if (executed.Exception is not null && !executed.ExceptionHandled)
        {
            return false;
        }

        return executed.Result switch
        {
            IStatusCodeActionResult { StatusCode: { } status } => status is >= 200 and < 300,
            _ => true
        };
    }

    // The target of a user action: its userId argument, or (UpdateUser without one) the caller.
    private static Guid UserIdOf(ActionExecutingContext context)
        => context.ActionArguments.TryGetValue("userId", out var value) && value is Guid id && !id.Equals(Guid.Empty)
            ? id
            : context.HttpContext.User.GetUserId();

    private static bool IsAdministratorOrApiKey(ClaimsPrincipal caller) => caller.IsApiKey() || caller.IsInRole(AdministratorRole);

    private async Task<IDisposable?> TakeLockAsync(Guid userId, string action, CancellationToken cancellationToken)
    {
        var lease = await _locks.TryAcquireAsync(userId, _lockTimeout, cancellationToken).ConfigureAwait(false);
        if (lease is null)
        {
            _status.Hook(LibrarySyncStatus.LockTimeouts);
            _logger.LogWarning("An admin's {Action} of user {UserId} went on without Access Roles' policy lock: it was busy for {Seconds} s (fail open)", action, userId, _lockTimeout.TotalSeconds);
        }

        return lease;
    }

    private AuditActor Actor(ClaimsPrincipal caller) => AuditActor.Of(caller, id => _users.Find(id)?.Name);

    private async Task PreMergeAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        // A body that failed binding or validation (UpdateUserPolicy without its provider ids) passes
        // through, and the core answers its own 400.
        var userId = UserIdOf(context);
        if (!context.ModelState.IsValid
            || !context.ActionArguments.TryGetValue("newPolicy", out var value)
            || value is not UserPolicy policy
            || userId.Equals(Guid.Empty))
        {
            await next().ConfigureAwait(false);
            return;
        }

        var lease = await TakeLockAsync(userId, "policy save", context.HttpContext.RequestAborted).ConfigureAwait(false);
        try
        {
            var posted = LibraryFields.Of(policy.EnableAllFolders, policy.EnabledFolders ?? []);
            PreMergeDecision? decision = null;
            string? error = null;
            try
            {
                decision = _sync.DecidePreMerge(userId, posted, policy.IsAdministrator, policy.IsDisabled);
                if (decision.Merge is { } merge)
                {
                    // Only the two library fields: IsAdministrator, IsDisabled and everything else stay the admin's.
                    policy.EnableAllFolders = merge.EnableAllFolders;
                    policy.EnabledFolders = merge.EnableAllFolders ? [] : [.. merge.EnabledFolders];
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Fail open: an admin can always save a user's policy, a broken plugin never stops it.
                error = ex.Message;
                decision = null;
                _logger.LogError(ex, "Access Roles' pre-merge of an admin's policy save of user {UserId} failed; the save goes through unchanged", userId);
            }

            var executed = await next().ConfigureAwait(false);
            try
            {
                var actor = Actor(context.HttpContext.User);
                if (error is not null)
                {
                    await _sync.AuditHookFailedAsync(userId, actor, posted, error).ConfigureAwait(false);
                }
                else if (decision is not null && Succeeded(executed))
                {
                    await _sync.RecordPreMergeAsync(decision, actor, CancellationToken.None).ConfigureAwait(false);
                }
                else if (decision?.Merge is not null)
                {
                    // The save failed (a concurrent save came first, 10.11.5's DbUpdateConcurrencyException): the next run decides.
                    _service.Request(SyncTrigger.Retry, LibrarySyncService.RetryDelay);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _status.Hook(LibrarySyncStatus.FilterErrors);
                _logger.LogError(ex, "Access Roles couldn't record the pre-merged policy save of user {UserId}", userId);
            }
        }
        finally
        {
            lease?.Dispose();
        }
    }

    private async Task LockAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var caller = context.HttpContext.User;
        var userId = UserIdOf(context);
        if (!IsAdministratorOrApiKey(caller) || userId.Equals(Guid.Empty))
        {
            await next().ConfigureAwait(false);
            return;
        }

        var lease = await TakeLockAsync(userId, ((ControllerActionDescriptor)context.ActionDescriptor).ActionName, context.HttpContext.RequestAborted).ConfigureAwait(false);
        try
        {
            await next().ConfigureAwait(false);
        }
        finally
        {
            lease?.Dispose();
        }
    }

    private async Task SignedInAsync(ActionExecutingContext context, ActionExecutedContext executed)
    {
        if (executed.Exception is not null && !executed.ExceptionHandled)
        {
            return;
        }

        if (executed.Result is not ObjectResult { Value: AuthenticationResult result } || result.User is not { } dto || dto.Id.Equals(Guid.Empty))
        {
            return;
        }

        var userId = dto.Id;
        UserCorrection? correction = null;
        string? error = null;
        try
        {
            correction = await _sync.CorrectUserAsync(userId, SyncTrigger.SignIn, _signInWait, context.HttpContext.TraceIdentifier, context.HttpContext.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error = ex.Message;
            _status.Hook(LibrarySyncStatus.FilterErrors);
            _logger.LogError(ex, "Access Roles couldn't correct the library access of user {UserId} at their sign-in", userId);
        }

        if (correction is { Managed: false } || (correction is null && _sync.IsAssigned(userId) is not true))
        {
            return;
        }

        foreach (var later in SignInFollowUps)
        {
            _service.Request(SyncTrigger.SignIn, later);
        }

        // The response's User as the server enforces it now: after the hook's write, and after a run's that came while the
        // hook waited for the user's lock.
        var native = _sync.EnforcedNow(userId);
        if (native is not null && dto.Policy is { } shown && !native.Same(shown.EnableAllFolders, shown.EnabledFolders ?? []))
        {
            Refresh(context, result, userId);
        }

        if (correction is { Failed: false })
        {
            return;
        }

        // The correction failed or the store holds the sync. Never lock out an administrator; refuse anyone else whose access
        // is more than the rules give, or than the last write while held (unknown when the hook threw: fail closed; nothing to
        // correct when the rules themselves write nothing, the no-libraries refusal or the lockout guard).
        var planned = correction?.Planned;
        var listed = _sync.ListedOrEmpty();
        var broader = correction is null
            || (planned is not null && (native is null || listed.Expand(native.EnableAllFolders, native.EnabledFolders).Except(listed.Expand(planned.EnableAllFolders, planned.EnabledFolders)).Any()));
        if (dto.Policy?.IsAdministrator == true || !broader)
        {
            if (correction?.HeldBecause is null)
            {
                _logger.LogWarning("Access Roles couldn't correct the library access of user {UserId} at their sign-in; the runs that follow try again", userId);
            }

            return;
        }

        await RefuseAsync(executed, result, userId, dto.Name, correction, native, error).ConfigureAwait(false);
    }

    // The response's User: a fresh DTO, so it carries the corrected policy (the core's is stale).
    private void Refresh(ActionExecutingContext context, AuthenticationResult result, Guid userId)
    {
        try
        {
            if (KitUsers.Find(_userManager, userId) is { } user)
            {
                result.User = _userManager.GetUserDto(user, context.HttpContext.GetNormalizedRemoteIP().ToString());
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Access Roles couldn't refresh the sign-in response of user {UserId}; its policy is the one from before the correction", userId);
        }
    }

    private async Task RefuseAsync(ActionExecutedContext executed, AuthenticationResult result, Guid userId, string? userName, UserCorrection? correction, LibraryFields? native, string? error)
    {
        if (correction?.HeldBecause is { } held)
        {
            _logger.LogWarning("Access Roles refused the sign-in of user {UserId}: the library sync is held ({Reason}) and their library access gives more than Access Roles last wrote (fail closed)", userId, held);
        }
        else
        {
            _logger.LogWarning("Access Roles refused the sign-in of user {UserId}: their library access gives more than their roles and couldn't be corrected (fail closed)", userId);
        }

        // The audit says whether the token is really gone.
        var revoked = false;
        try
        {
            if (_sessions is not null && !string.IsNullOrEmpty(result.AccessToken))
            {
                await _sessions.Logout(result.AccessToken).ConfigureAwait(false);
                revoked = true;
            }
            else
            {
                _logger.LogWarning("Access Roles couldn't revoke the token of the refused sign-in of user {UserId}: no session manager or no token", userId);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Access Roles couldn't revoke the token of the refused sign-in of user {UserId}", userId);
        }

        executed.Result = KitProblemDetails.Result(PluginErrorKind.Unavailable, SignInRefusedMessage);
        executed.HttpContext.Response.Headers.RetryAfter = (correction?.HeldBecause is null ? RetryAfterSeconds : HeldRetryAfterSeconds).ToString(CultureInfo.InvariantCulture);
        try
        {
            await _sync.AuditSignInRefusedAsync(userId, userName, correction, native, error, revoked).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Access Roles couldn't audit the refused sign-in of user {UserId}", userId);
        }
    }
}
