using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AccessRoles.Policy;

/// <summary>
/// <see cref="ICommittedUsers"/> over the server's <c>IDbContextFactory&lt;JellyfinDbContext&gt;</c> (read only,
/// <c>AsNoTracking</c>): the user row with its permissions, preferences and access
/// schedules, in a context of its own that tracks nothing and saves nothing.
/// </summary>
internal sealed class DatabaseCommittedUsers : ICommittedUsers
{
    private readonly IDbContextFactory<JellyfinDbContext> _factory;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DatabaseCommittedUsers"/> class.
    /// </summary>
    /// <param name="factory">The server's database context factory.</param>
    /// <param name="logger">The logger.</param>
    public DatabaseCommittedUsers(IDbContextFactory<JellyfinDbContext> factory, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(logger);
        _factory = factory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<CommittedUser> ReadAsync(Guid userId, CancellationToken cancellationToken)
    {
        try
        {
            var context = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using (context.ConfigureAwait(false))
            {
                var user = await context.Users
                    .AsNoTracking()
                    .AsSplitQuery()
                    .Include(u => u.Permissions)
                    .Include(u => u.Preferences)
                    .Include(u => u.AccessSchedules)
                    .FirstOrDefaultAsync(u => u.Id.Equals(userId), cancellationToken)
                    .ConfigureAwait(false);
                return user is null ? CommittedUser.Missing : new CommittedUser(true, user);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Access Roles couldn't read the stored row of user {UserId}", userId);
            return CommittedUser.Unavailable;
        }
    }
}
