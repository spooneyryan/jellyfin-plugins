using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Jellyfin.Plugin.TagCloud.Api.Models;
using Jellyfin.Plugin.TagCloud.Configuration;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TagCloud.Index;

/// <summary>
/// The index of which titles carry each word: built in full by <see cref="TagCloudIndexWorker"/> at
/// startup, daily and after large changes, patched title by title by <see cref="TagCloudLibraryConsumer"/>, and read by
/// every request through <see cref="Current"/>. Builds and patches take one lock, so a patch that arrives during a build
/// waits for it and is applied after.
/// </summary>
internal sealed class TagCloudIndex : IDisposable
{
    private readonly TitleReader _titles;
    private readonly PeopleReader _people;
    private readonly IPluginConfigurationProvider _configuration;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Channel<bool> _rebuilds = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    private volatile TagCloudIndexSnapshot? _current;
    private IndexState? _state;
    private long _generation;
    private DateTime? _builtUtc;
    private volatile string _stateName = "NotBuilt";
    private int _done;
    private int _total;
    private long _buildMilliseconds;
    private long _peopleMilliseconds;
    private string _peoplePath = string.Empty;
    private int _patched;
    private string? _lastError;

    /// <summary>
    /// Initializes a new instance of the <see cref="TagCloudIndex"/> class.
    /// </summary>
    /// <param name="titles">The title reader.</param>
    /// <param name="people">The people reader.</param>
    /// <param name="configuration">The settings (which sources are on).</param>
    /// <param name="logger">The logger.</param>
    /// <param name="time">The clock.</param>
    public TagCloudIndex(TitleReader titles, PeopleReader people, IPluginConfigurationProvider configuration, ILogger logger, TimeProvider? time = null)
    {
        _titles = titles;
        _people = people;
        _configuration = configuration;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Gets the snapshot requests read, or null until the first build finishes.</summary>
    public TagCloudIndexSnapshot? Current => _current;

    /// <summary>Gets how many titles the build in progress has read.</summary>
    public int Done => Volatile.Read(ref _done);

    /// <summary>Gets how many titles the build in progress reads.</summary>
    public int Total => Volatile.Read(ref _total);

    /// <summary>
    /// Gets the sources the settings turn on, in the toolbar's order.
    /// </summary>
    /// <param name="configuration">The settings.</param>
    /// <returns>The sources.</returns>
    public static IReadOnlyList<TagCloudSource> EnabledSources(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var sources = new List<TagCloudSource>();
        if (configuration.EnableTags)
        {
            sources.Add(TagCloudSource.Tags);
        }

        if (configuration.EnableGenres)
        {
            sources.Add(TagCloudSource.Genres);
        }

        if (configuration.EnableActors)
        {
            sources.Add(TagCloudSource.Actors);
        }

        if (configuration.EnableDirectors)
        {
            sources.Add(TagCloudSource.Directors);
        }

        if (configuration.EnableWriters)
        {
            sources.Add(TagCloudSource.Writers);
        }

        if (configuration.EnableStudios)
        {
            sources.Add(TagCloudSource.Studios);
        }

        return sources;
    }

    /// <summary>
    /// Asks for a full rebuild; several asks before it starts make one.
    /// </summary>
    public void RequestRebuild() => _rebuilds.Writer.TryWrite(true);

    /// <summary>
    /// Waits for the next ask for a full rebuild.
    /// </summary>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <returns>A task that completes when a rebuild was asked for.</returns>
    public async Task WaitForRebuildRequestAsync(CancellationToken cancellationToken)
        => await _rebuilds.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Builds the index in full and publishes it.
    /// </summary>
    /// <param name="cancellationToken">Stops the build; the last snapshot stays.</param>
    /// <returns>A task that completes when the new snapshot is published.</returns>
    public async Task BuildAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var watch = Stopwatch.StartNew();
            _stateName = "Building";
            Volatile.Write(ref _done, 0);
            Volatile.Write(ref _total, 0);
            var sources = EnabledSources(_configuration.GetConfiguration());
            var titles = _titles.ReadAll();
            Volatile.Write(ref _total, titles.Count);

            var state = new IndexState(sources);
            var ordinals = new Dictionary<Guid, int>(titles.Count);
            foreach (var title in titles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var ordinal = state.Upsert(TitleReader.ToRecord(title));
                ordinals[title.Id] = ordinal;
                SetOwnWords(state, ordinal, title, sources);
                Interlocked.Increment(ref _done);
            }

            if (sources.Any(PeopleReader.IsPeople))
            {
                foreach (var (itemId, bySource) in _people.ReadAll(titles))
                {
                    if (!ordinals.TryGetValue(itemId, out var ordinal))
                    {
                        continue;
                    }

                    foreach (var (source, names) in bySource)
                    {
                        state.SetWords(ordinal, source, names);
                    }
                }

                _peopleMilliseconds = (long)_people.LastDuration.TotalMilliseconds;
                _peoplePath = _people.LastPath;
            }

            _state = state;
            _builtUtc = _time.GetUtcNow().UtcDateTime;
            _patched = 0;
            Publish();
            _buildMilliseconds = watch.ElapsedMilliseconds;
            _lastError = null;
            _stateName = "Ready";
            _logger.LogInformation("Indexed {Titles} titles for the tag cloud in {Milliseconds} ms (people: {PeoplePath}, {PeopleMilliseconds} ms)", state.Count, _buildMilliseconds, _peoplePath, _peopleMilliseconds);
        }
        catch (OperationCanceledException)
        {
            _stateName = _current is null ? "NotBuilt" : "Ready";
            throw;
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            _stateName = "Failed";
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Patches the index for changed and removed titles: each changed one is read again, with its credits.
    /// Before the first build there is nothing to patch; the build reads them all.
    /// </summary>
    /// <param name="changed">Ids of titles added or updated.</param>
    /// <param name="removed">Ids of titles removed.</param>
    /// <param name="cancellationToken">Stops the patch.</param>
    /// <returns>How many titles changed in the index.</returns>
    public async Task<int> PatchAsync(IReadOnlyCollection<Guid> changed, IReadOnlyCollection<Guid> removed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changed);
        ArgumentNullException.ThrowIfNull(removed);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = _state;
            if (state is null)
            {
                return 0;
            }

            var count = 0;
            foreach (var id in removed)
            {
                count += state.Remove(id) ? 1 : 0;
            }

            var people = state.Sources.Any(PeopleReader.IsPeople);
            foreach (var id in changed.Except(removed))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var title = _titles.Read(id);
                if (title is null)
                {
                    count += state.Remove(id) ? 1 : 0;
                    continue;
                }

                var ordinal = state.Upsert(TitleReader.ToRecord(title));
                SetOwnWords(state, ordinal, title, state.Sources);
                if (people)
                {
                    var credits = _people.ReadFor(title);
                    foreach (var source in state.Sources.Where(PeopleReader.IsPeople))
                    {
                        state.SetWords(ordinal, source, credits.Where(c => c.Source == source).Select(c => c.Name));
                    }
                }

                count++;
            }

            if (count > 0)
            {
                _patched += count;
                Publish();
            }

            return count;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Describes the index for the admin.
    /// </summary>
    /// <returns>The status.</returns>
    public TagCloudStatusDto Describe()
    {
        var current = _current;
        return new TagCloudStatusDto
        {
            State = _stateName,
            Done = Done,
            Total = Total,
            Titles = current?.TitleCount ?? 0,
            Words = current?.CountWords() ?? new Dictionary<string, int>(),
            BuiltUtc = current?.BuiltUtc,
            BuildMilliseconds = _buildMilliseconds,
            PeopleMilliseconds = _peopleMilliseconds,
            PeoplePath = _peoplePath,
            Generation = current?.Generation ?? 0,
            Patched = _patched,
            LastError = _lastError
        };
    }

    /// <inheritdoc />
    public void Dispose() => _lock.Dispose();

    private static void SetOwnWords(IndexState state, int ordinal, BaseItem title, IEnumerable<TagCloudSource> sources)
    {
        foreach (var source in sources.Where(s => !PeopleReader.IsPeople(s)))
        {
            state.SetWords(ordinal, source, TitleReader.WordsOf(title, source));
        }
    }

    private void Publish() => _current = _state!.ToSnapshot(Interlocked.Increment(ref _generation), _builtUtc);
}
