using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Events.Users;
using Jellyfin.Plugin.AccessRoles.Services;
using MediaBrowser.Controller.Events;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AccessRoles.Sync;

/// <summary>
/// Default roles for new users (<c>UserCreatedConsumer</c>, one of the library sync's triggers): on
/// <c>UserCreatedEventArgs</c> (<c>v10.11.5:Jellyfin.Server.Implementations/Users/UserManager.cs:240</c>, raised inside the
/// create request, after the user is in the user manager) the user gets <c>DefaultRoleIds</c>
/// (<see cref="AccessRolesAdminService.GiveDefaultRolesAsync"/>), and the library sync runs <see cref="FirstRunDelay"/> after
/// the request that created the user has finished, and again <see cref="FollowUps"/> after it. Users created by the stock
/// <i>Add user</i> page, the API, LDAP or SSO plugins all pass here; the page's own policy post that follows is pre-merged
/// (<c>Policy/UserPolicyFilter</c>), and an LDAP or SSO sign-in's policy is corrected by the sign-in hook. Never throws
/// (Jellyfin's event manager logs a consumer's exception, but a failure here must not fail the user's creation).
/// </summary>
/// <remarks>
/// <para><b>Nothing writes the user while the request creating them runs</b> (not "reconcile at once", as first planned):
/// the event is raised before that request is done with the new user. <c>UserController.CreateUserByName</c> saves the
/// password next, through <c>UpdateUserAsync</c> on the entity it created
/// (<c>v10.11.5:Jellyfin.Api/Controllers/UserController.cs:543-549</c>); a policy write of ours in between moves the row
/// version on, so that save fails (<c>DbUpdateConcurrencyException</c>, HTTP 500), the user keeps no password (an empty one
/// signs in), and the cache keeps the stale entity. So the user is marked as being created
/// (<see cref="LibrarySync.BeginCreation"/>) before the roles are saved, which skips them in every run, post-write check and
/// other request's sign-in until the response has completed (<c>HttpResponse.OnCompleted</c>; should it never say so, the
/// mark lapses after <see cref="LibrarySync.CreationTimeout"/> and the next run, the backstop's at the latest, writes them),
/// and the default roles' save raises no store change (its run, 2 s later, answered 500 whenever the request
/// took longer, a busy database for instance).</para>
/// <para><b>The first run <see cref="FirstRunDelay"/> after the request</b>, not at once: a client that signs the new user in
/// right after the creation would meet that run's write (the 10.11.5 sign-in race: 500, and the stale object enforced
/// until the next write). The runs leave the user alone for that long; their sign-in is corrected by the hook, and the
/// <i>Add user</i> page's own save is pre-merged.</para>
/// </remarks>
internal sealed class UserCreatedConsumer : IEventConsumer<UserCreatedEventArgs>
{
    /// <summary>The first run's delay after the creating request finished (or after the event, outside a request).</summary>
    public static readonly TimeSpan FirstRunDelay = TimeSpan.FromSeconds(3);

    /// <summary>The runs after the first one, from the same moment ("again after 5 s and 30 s").</summary>
    public static readonly IReadOnlyList<TimeSpan> FollowUps = [TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];

    private readonly AccessRolesAdminService _admin;
    private readonly LibrarySync _sync;
    private readonly LibrarySyncService _service;
    private readonly LibrarySyncStatus _status;
    private readonly IHttpContextAccessor? _http;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="UserCreatedConsumer"/> class.
    /// </summary>
    /// <param name="admin">The roles API, which saves the assignment.</param>
    /// <param name="sync">The library sync, which leaves the user alone while they are being created.</param>
    /// <param name="service">The sync's scheduler.</param>
    /// <param name="status">What Diagnostics shows.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="http">The request the user is created in, if any; <c>null</c> runs the sync <see cref="FirstRunDelay"/> later.</param>
    public UserCreatedConsumer(AccessRolesAdminService admin, LibrarySync sync, LibrarySyncService service, LibrarySyncStatus status, ILogger logger, IHttpContextAccessor? http = null)
    {
        ArgumentNullException.ThrowIfNull(admin);
        ArgumentNullException.ThrowIfNull(sync);
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(logger);
        _admin = admin;
        _sync = sync;
        _service = service;
        _status = status;
        _http = http;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task OnEvent(UserCreatedEventArgs eventArgs)
    {
        if (eventArgs?.Argument is not { } user)
        {
            return;
        }

        var context = _http?.HttpContext;
        var given = false;
        try
        {
            // Before the roles are saved: no run that starts meanwhile may write the user.
            _sync.BeginCreation(user.Id, context?.TraceIdentifier);
            if (context is not null)
            {
                // After the creating request is done with the user (its password saved, the answer sent).
                context.Response.OnCompleted(() =>
                {
                    Created(user.Id, given);
                    return Task.CompletedTask;
                });
            }

            given = await _admin.GiveDefaultRolesAsync(user.Id, CancellationToken.None).ConfigureAwait(false);
            if (given)
            {
                _status.Hook(LibrarySyncStatus.DefaultRolesGiven);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The user stays created; without the roles they're unmanaged until an admin assigns them.
            _status.Hook(LibrarySyncStatus.FilterErrors);
            _logger.LogError(ex, "Access Roles couldn't give the new user {UserId} the default roles", user.Id);
        }

        if (context is null)
        {
            Created(user.Id, given);
        }
    }

    // The creating request is done with the user: a user given the default roles is left alone FirstRunDelay longer, then
    // the runs write them.
    private void Created(Guid userId, bool given)
    {
        try
        {
            _sync.EndCreation(userId, given ? FirstRunDelay : TimeSpan.Zero);
            if (!given)
            {
                return;
            }

            _service.Request(SyncTrigger.UserCreated, FirstRunDelay);
            foreach (var later in FollowUps)
            {
                _service.Request(SyncTrigger.UserCreated, later);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Access Roles couldn't ask for the library sync of the new user {UserId}", userId);
        }
    }
}
