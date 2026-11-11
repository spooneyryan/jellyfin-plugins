using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Database.Implementations;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.AccessRoles.Enforcement;

/// <summary>
/// <see cref="IPeopleLibraries"/> over the server's <c>IDbContextFactory&lt;JellyfinDbContext&gt;</c> (read only,
/// <c>AsNoTracking</c>, a context of its own that saves nothing): the page's names joined to <c>PeopleBaseItemMap</c> and
/// the items' <c>TopParentId</c> within the caller's libraries, what 12.0's <c>PeopleRepository</c> does with its
/// <c>AccessFilter</c> (<c>git:9a258c089d</c>). Library access only: a name whose only items there are
/// blocked by parental rules is kept, as it is without the plugin. Tied to the 10.11 schema; the rule is 10.11-only
/// (12.x filters people natively), and a query that throws refuses the request (fail closed).
/// </summary>
internal sealed class DatabasePeopleLibraries : IPeopleLibraries
{
    private readonly IDbContextFactory<JellyfinDbContext> _factory;

    /// <summary>
    /// Initializes a new instance of the <see cref="DatabasePeopleLibraries"/> class.
    /// </summary>
    /// <param name="factory">The server's database context factory.</param>
    public DatabasePeopleLibraries(IDbContextFactory<JellyfinDbContext> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    /// <inheritdoc />
    public IReadOnlySet<string> NamesWithItemsIn(IReadOnlyCollection<string> names, IReadOnlyCollection<Guid> topParentIds)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(topParentIds);
        if (names.Count == 0 || topParentIds.Count == 0)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var wanted = names.ToArray();
        var libraries = topParentIds.ToArray();
        using var context = _factory.CreateDbContext();
        return context.PeopleBaseItemMap
            .AsNoTracking()
            .Where(m => wanted.Contains(m.People.Name) && m.Item.TopParentId != null && libraries.Contains(m.Item.TopParentId.Value))
            .Select(m => m.People.Name)
            .Distinct()
            .ToHashSet(StringComparer.Ordinal);
    }
}
