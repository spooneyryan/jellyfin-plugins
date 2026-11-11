using System;
using System.Threading.Tasks;
using Jellyfin.Data.Events.Users;
using Jellyfin.Plugin.AccessRoles.Access;
using Jellyfin.Plugin.AccessRoles.Policy;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Events;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AccessRoles.Sync;

/// <summary>
/// The 12.1+ trigger (<c>UserUpdatedConsumer</c>): Jellyfin 12.1 and later publish
/// <c>UserUpdatedEventArgs</c> for every <c>UpdatePolicyAsync</c>, Access Roles' own writes included, so
/// a policy change made outside MVC (another plugin, a provider) asks for a run (debounced like a store change), unless
/// the event shows the writer's own recent write (the loop guard, <see cref="PolicyWriter.IsOwnRecentWrite"/>). On 10.11.x
/// the event is only raised by a rename and this consumer does nothing; the 10.11 build is inert on 12 anyway (the ABI
/// guard), so this is the 12 build's trigger, kept here with its test. It never writes: a run under the user's lock does,
/// never inside the event (12.1 publishes it inside <c>UpdatePolicyAsync</c>, possibly under the filter's lock).
/// </summary>
internal sealed class UserUpdatedConsumer : IEventConsumer<UserUpdatedEventArgs>
{
    /// <summary>The first Jellyfin that publishes the event for policy writes.</summary>
    public static readonly Version FirstVersion = new(12, 1);

    private readonly IServerApplicationHost _host;
    private readonly LibrarySync _sync;
    private readonly PolicyWriter _writer;
    private readonly LibrarySyncService _service;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="UserUpdatedConsumer"/> class.
    /// </summary>
    /// <param name="host">The server, for its version.</param>
    /// <param name="sync">The library sync, for whether the user is managed.</param>
    /// <param name="writer">The policy writer, for the loop guard.</param>
    /// <param name="service">The sync's scheduler.</param>
    /// <param name="logger">The logger.</param>
    public UserUpdatedConsumer(IServerApplicationHost host, LibrarySync sync, PolicyWriter writer, LibrarySyncService service, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(sync);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(logger);
        _host = host;
        _sync = sync;
        _writer = writer;
        _service = service;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task OnEvent(UserUpdatedEventArgs eventArgs)
    {
        try
        {
            if (eventArgs?.Argument is not { } user || _host.ApplicationVersion < FirstVersion || _sync.IsAssigned(user.Id) is not true)
            {
                return Task.CompletedTask;
            }

            var fields = JellyfinServerUsers.From(user).Native.Libraries;
            if (!_writer.IsOwnRecentWrite(user.Id, fields))
            {
                _service.Request(SyncTrigger.UserUpdated, LibrarySyncService.Debounce);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Access Roles couldn't look at a user update event");
        }

        return Task.CompletedTask;
    }
}
