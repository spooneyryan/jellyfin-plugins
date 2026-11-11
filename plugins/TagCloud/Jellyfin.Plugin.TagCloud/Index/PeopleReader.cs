using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations;
using Jellyfin.Plugin.TagCloud.Api.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TagCloud.Index;

/// <summary>
/// Reads who is credited on the titles: one grouped, read-only read of <c>PeopleBaseItemMap</c> joined to
/// <c>Peoples</c> through the server's <see cref="IDbContextFactory{TContext}"/> (a path checked
/// and measured on 10.11.5), and, when that can't run (no factory, or the read throws: a schema change, a lock), the
/// library's <c>GetPeople(item)</c> for each title, which is slower but uses only the public API.
/// </summary>
/// <remarks>
/// Only the 10.11 schema is read here (<c>v10.11.5:src/Jellyfin.Database/Jellyfin.Database.Implementations/
/// JellyfinDbContext.cs:139-144</c>: a person row per name and type, <c>People.PersonType</c> "Actor", "Director",
/// "Writer"); the Jellyfin 12 port gives this file its own build.
/// </remarks>
internal sealed class PeopleReader
{
    /// <summary>The path name of the grouped read.</summary>
    public const string DatabasePath = "Database";

    /// <summary>The path name of the per-title fallback.</summary>
    public const string PerItemPath = "PerItem";

    private static readonly string[] RoleNames = [nameof(PersonKind.Actor), nameof(PersonKind.Director), nameof(PersonKind.Writer)];
    private static readonly string[] TitleTypes = [typeof(Movie).FullName!, typeof(MediaBrowser.Controller.Entities.TV.Series).FullName!];

    private readonly ILibraryManager _library;
    private readonly Func<IDbContextFactory<JellyfinDbContext>?> _database;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PeopleReader"/> class.
    /// </summary>
    /// <param name="library">The library manager, for the fallback and for one title's patch.</param>
    /// <param name="database">The server's database context factory, or null when there is none.</param>
    /// <param name="logger">The logger.</param>
    public PeopleReader(ILibraryManager library, Func<IDbContextFactory<JellyfinDbContext>?> database, ILogger logger)
    {
        _library = library;
        _database = database;
        _logger = logger;
    }

    /// <summary>Gets the path the last full read took: <see cref="DatabasePath"/> or <see cref="PerItemPath"/>.</summary>
    public string LastPath { get; private set; } = string.Empty;

    /// <summary>Gets how long the last full read took.</summary>
    public TimeSpan LastDuration { get; private set; }

    /// <summary>
    /// Gets the source a credit counts in.
    /// </summary>
    /// <param name="kind">The person's kind on the title.</param>
    /// <returns>Actors, Directors or Writers; null for any other kind.</returns>
    public static TagCloudSource? SourceOf(PersonKind kind) => kind switch
    {
        PersonKind.Actor => TagCloudSource.Actors,
        PersonKind.Director => TagCloudSource.Directors,
        PersonKind.Writer => TagCloudSource.Writers,
        _ => null
    };

    /// <summary>
    /// Gets whether a source is one of people.
    /// </summary>
    /// <param name="source">The source.</param>
    /// <returns>Whether it is.</returns>
    public static bool IsPeople(TagCloudSource source) => source is TagCloudSource.Actors or TagCloudSource.Directors or TagCloudSource.Writers;

    /// <summary>
    /// Reads the credits of every title.
    /// </summary>
    /// <param name="titles">The titles.</param>
    /// <returns>For each title with credits, the names in each people source.</returns>
    public IReadOnlyDictionary<Guid, Dictionary<TagCloudSource, List<string>>> ReadAll(IReadOnlyCollection<BaseItem> titles)
    {
        ArgumentNullException.ThrowIfNull(titles);
        var watch = Stopwatch.StartNew();
        var wanted = titles.Select(t => t.Id).ToHashSet();
        Dictionary<Guid, Dictionary<TagCloudSource, List<string>>> result;
        try
        {
            result = ReadFromDatabase(wanted);
            LastPath = DatabasePath;
        }
        catch (Exception ex)
        {
            // Any failure of the grouped read falls back to the public API.
            _logger.LogWarning(ex, "Reading the credits in one grouped read failed; reading them title by title instead");
            result = [];
            foreach (var title in titles)
            {
                Add(result, title.Id, ReadFor(title));
            }

            LastPath = PerItemPath;
        }

        LastDuration = watch.Elapsed;
        return result;
    }

    /// <summary>
    /// Reads one title's credits through the library (a patch).
    /// </summary>
    /// <param name="title">The title.</param>
    /// <returns>The names in each people source.</returns>
    public IReadOnlyList<(TagCloudSource Source, string Name)> ReadFor(BaseItem title)
        => _library.GetPeople(title)
            .Select(p => (Source: SourceOf(p.Type), p.Name))
            .Where(p => p.Source is not null && !string.IsNullOrWhiteSpace(p.Name))
            .Select(p => (p.Source!.Value, p.Name))
            .ToList();

    private static void Add(Dictionary<Guid, Dictionary<TagCloudSource, List<string>>> result, Guid itemId, IEnumerable<(TagCloudSource Source, string Name)> credits)
    {
        foreach (var (source, name) in credits)
        {
            if (!result.TryGetValue(itemId, out var bySource))
            {
                result[itemId] = bySource = [];
            }

            if (!bySource.TryGetValue(source, out var names))
            {
                bySource[source] = names = [];
            }

            names.Add(name);
        }
    }

    private Dictionary<Guid, Dictionary<TagCloudSource, List<string>>> ReadFromDatabase(HashSet<Guid> wanted)
    {
        var factory = _database() ?? throw new InvalidOperationException("The server has no database context factory.");
        using var context = factory.CreateDbContext();
        var rows = context.PeopleBaseItemMap.AsNoTracking()
            .Where(m => TitleTypes.Contains(m.Item.Type) && m.People.PersonType != null && RoleNames.Contains(m.People.PersonType))
            .Select(m => new { m.ItemId, m.People.Name, m.People.PersonType })
            .ToList();

        var result = new Dictionary<Guid, Dictionary<TagCloudSource, List<string>>>();
        foreach (var row in rows)
        {
            if (wanted.Contains(row.ItemId) && Enum.TryParse<PersonKind>(row.PersonType, out var kind) && SourceOf(kind) is { } source && !string.IsNullOrWhiteSpace(row.Name))
            {
                Add(result, row.ItemId, [(source, row.Name)]);
            }
        }

        return result;
    }
}
