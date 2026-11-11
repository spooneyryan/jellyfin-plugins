using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Kit.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kit.Diagnostics;

/// <summary>
/// The plugin's web health reports. Browsers post a report when an anchor
/// they expected is missing; any signed-in user can post, so the registry trusts none of it:
/// <list type="bullet">
/// <item><description>every field is validated (<see cref="ClientHealthReport.TryParse"/>) against the plugin's anchor list
/// and fixed patterns, and an invalid report is dropped;</description></item>
/// <item><description>one report per user per <see cref="RateWindow"/> is accepted, the others are dropped;</description></item>
/// <item><description>one user's accepted reports name at most <see cref="MaxWebClientsPerUser"/> web clients
/// (<see cref="ClientHealthReport.WebKey"/>) per process, so one account can't fill Diagnostics with invented
/// versions;</description></item>
/// <item><description>a Warning is logged once per anchor and web version (<see cref="ClientHealthReport.WebKey"/>) per
/// process, the de-duplication set capped at <see cref="MaxLoggedKeys"/>;</description></item>
/// <item><description>the accepted reports are aggregated for the Diagnostics tab, at most <see cref="MaxEntries"/>
/// entries, in memory only; when the list is full, the entry updated longest ago makes room for a new one, so forged
/// reports can't keep a new problem out of it until the server restarts.</description></item>
/// </list>
/// Dropped reports are only counted (<see cref="Counts"/>) and logged at Debug; the caller always answers 204.
/// </summary>
internal sealed class ClientHealthRegistry
{
    /// <summary>The logger component name (<see cref="KitOptions.GetLoggerCategory"/>).</summary>
    public const string LoggerComponent = "ClientHealth";

    /// <summary>The most (anchor, web version) pairs logged per process.</summary>
    public const int MaxLoggedKeys = 200;

    /// <summary>The most Diagnostics entries kept.</summary>
    public const int MaxEntries = 200;

    /// <summary>The most distinct users counted per entry.</summary>
    public const int MaxUsersPerEntry = 1000;

    /// <summary>
    /// The most web clients (<see cref="ClientHealthReport.WebKey"/>: a web version, or <c>native:&lt;Client&gt;</c>) one
    /// user's accepted reports may name per process. A browser sends the server's one web version and each native app its
    /// own name, so a real user needs one to three.
    /// </summary>
    public const int MaxWebClientsPerUser = 4;

    /// <summary>How long a user waits between two accepted reports.</summary>
    public static readonly TimeSpan RateWindow = TimeSpan.FromMinutes(10);

    private readonly object _gate = new();
    private readonly KitOptions _options;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly Dictionary<Guid, DateTimeOffset> _lastAccepted = [];
    private readonly Dictionary<Guid, HashSet<string>> _webClients = [];
    private readonly HashSet<string> _logged = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Aggregate> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<ClientHealthOutcome, long> _counts = Enum.GetValues<ClientHealthOutcome>().ToDictionary(o => o, _ => 0L);
    private long _updates;
    private bool _loggedFull;

    /// <summary>
    /// Initializes a new instance of the <see cref="ClientHealthRegistry"/> class with the anchors embedded in the plugin.
    /// </summary>
    /// <param name="options">The plugin's kit options.</param>
    /// <param name="loggerFactory">The logger factory.</param>
    public ClientHealthRegistry(KitOptions options, ILoggerFactory loggerFactory)
        : this(options, WebAnchorCatalog.Load(options?.PluginAssembly!, typeof(ClientHealthRegistry).Assembly), loggerFactory, TimeProvider.System)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ClientHealthRegistry"/> class (tests). Internal, so dependency injection
    /// only sees the public constructor.
    /// </summary>
    /// <param name="options">The plugin's kit options.</param>
    /// <param name="catalog">The anchors reports may name.</param>
    /// <param name="loggerFactory">The logger factory.</param>
    /// <param name="timeProvider">The clock of the rate limit.</param>
    internal ClientHealthRegistry(KitOptions options, WebAnchorCatalog catalog, ILoggerFactory loggerFactory, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _options = options;
        Catalog = catalog;
        _logger = options.CreateLogger(loggerFactory, LoggerComponent);
        _time = timeProvider;
        if (catalog.Problems.Count > 0)
        {
            _logger.LogError(
                "The {Plugin} web anchor list has {Count} problem(s); reports naming the affected anchors are dropped: {Problems}",
                options.PluginName,
                catalog.Problems.Count,
                string.Join(" | ", catalog.Problems));
        }
    }

    /// <summary>
    /// Gets the anchors reports may name.
    /// </summary>
    public WebAnchorCatalog Catalog { get; }

    /// <summary>
    /// Gets how many reports ended in each outcome since the server started.
    /// </summary>
    public IReadOnlyDictionary<ClientHealthOutcome, long> Counts
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<ClientHealthOutcome, long>(_counts);
            }
        }
    }

    /// <summary>
    /// Gets how many (anchor, web version) pairs have been logged; never more than <see cref="MaxLoggedKeys"/>.
    /// </summary>
    public int LoggedKeys
    {
        get
        {
            lock (_gate)
            {
                return _logged.Count;
            }
        }
    }

    /// <summary>
    /// Gets the accepted reports, one entry per anchor, view and web version, most recent first.
    /// </summary>
    public IReadOnlyList<ClientHealthEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.Values.Select(e => e.ToEntry()).OrderByDescending(e => e.LastUtc).ThenBy(e => e.Anchor, StringComparer.Ordinal).ToList();
            }
        }
    }

    /// <summary>
    /// Validates, rate-limits, logs and records a report body.
    /// </summary>
    /// <param name="userId">The signed-in user who posted it.</param>
    /// <param name="body">The request body.</param>
    /// <returns>What happened to it.</returns>
    public ClientHealthOutcome Submit(Guid userId, ReadOnlyMemory<byte> body)
    {
        if (!ClientHealthReport.TryParse(body, Catalog, out var report, out var reason))
        {
            _logger.LogDebug("Dropped a {Plugin} client health report of user {UserId}: {Reason}", _options.PluginName, userId, reason);
            return Count(ClientHealthOutcome.Invalid);
        }

        return Submit(userId, report!);
    }

    /// <summary>
    /// Rate-limits, logs and records a validated report.
    /// </summary>
    /// <param name="userId">The signed-in user who posted it.</param>
    /// <param name="report">The report.</param>
    /// <returns>What happened to it.</returns>
    public ClientHealthOutcome Submit(Guid userId, ClientHealthReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var now = _time.GetUtcNow();
        var toLog = new List<(ClientHealthMissing Missing, string View)>();
        var full = false;
        lock (_gate)
        {
            if (_lastAccepted.TryGetValue(userId, out var last) && now - last < RateWindow && now >= last)
            {
                _logger.LogDebug("Dropped a {Plugin} client health report of user {UserId}: one is accepted per user every {Minutes} minutes", _options.PluginName, userId, RateWindow.TotalMinutes);
                return CountLocked(ClientHealthOutcome.RateLimited);
            }

            if (!_webClients.TryGetValue(userId, out var webClients))
            {
                webClients = new HashSet<string>(StringComparer.Ordinal);
                _webClients[userId] = webClients;
            }

            if (!webClients.Contains(report.WebKey) && webClients.Count >= MaxWebClientsPerUser)
            {
                _logger.LogDebug("Dropped a {Plugin} client health report of user {UserId}: one user's reports name at most {Count} web clients per server process", _options.PluginName, userId, MaxWebClientsPerUser);
                return CountLocked(ClientHealthOutcome.TooManyWebClients);
            }

            webClients.Add(report.WebKey);
            _lastAccepted[userId] = now;
            PruneRateLimit(now);
            foreach (var missing in report.Missing)
            {
                Record(userId, report, missing, now);
                var key = missing.Anchor + "|" + report.WebKey;
                if (_logged.Contains(key))
                {
                    continue;
                }

                if (_logged.Count < MaxLoggedKeys)
                {
                    _logged.Add(key);
                    toLog.Add((missing, missing.View));
                }
                else if (!_loggedFull)
                {
                    _loggedFull = true;
                    full = true;
                }
            }

            CountLocked(ClientHealthOutcome.Accepted);
        }

        foreach (var (missing, view) in toLog)
        {
            _logger.LogWarning(
                "{Plugin} web anchor {Anchor} is missing on the {View} view ({Web}), as reported by a browser; that feature is off there until the web client or the plugin changes. Diagnostics lists the reports",
                _options.PluginName,
                missing.Anchor,
                view,
                report.WebText);
        }

        if (full)
        {
            _logger.LogWarning("{Plugin} has logged {Count} missing web anchors in this process; further ones are only listed in Diagnostics", _options.PluginName, MaxLoggedKeys);
        }

        return ClientHealthOutcome.Accepted;
    }

    /// <summary>
    /// Counts a report the endpoint dropped before reading it (<see cref="ClientHealthOutcome.Disabled"/>,
    /// <see cref="ClientHealthOutcome.TooLarge"/>).
    /// </summary>
    /// <param name="outcome">What happened to the report.</param>
    /// <returns><paramref name="outcome"/>, for a return statement.</returns>
    public ClientHealthOutcome Count(ClientHealthOutcome outcome)
    {
        lock (_gate)
        {
            return CountLocked(outcome);
        }
    }

    private ClientHealthOutcome CountLocked(ClientHealthOutcome outcome)
    {
        _counts[outcome]++;
        return outcome;
    }

    private void Record(Guid userId, ClientHealthReport report, ClientHealthMissing missing, DateTimeOffset now)
    {
        var key = missing.Anchor + "|" + missing.View + "|" + report.WebKey;
        if (!_entries.TryGetValue(key, out var entry))
        {
            if (_entries.Count >= MaxEntries)
            {
                // The list is full: the entry updated longest ago makes room, so the reports that filled it (forged ones
                // included) can't keep a new problem out of Diagnostics until the server restarts.
                // A problem that browsers still see comes back with their next report.
                _entries.Remove(_entries.MinBy(p => p.Value.LastUpdate).Key);
                _counts[ClientHealthOutcome.Overflow]++;
            }

            entry = new Aggregate(missing.Anchor, missing.View, Catalog.Find(missing.Anchor)?.Required ?? false, report.WebVersion, now);
            _entries[key] = entry;
        }

        entry.Add(userId, report, missing.Count, now, ++_updates);
    }

    // Entries older than the window no longer limit anything; drop them once the map grows.
    private void PruneRateLimit(DateTimeOffset now)
    {
        if (_lastAccepted.Count <= 1000)
        {
            return;
        }

        foreach (var stale in _lastAccepted.Where(p => now - p.Value >= RateWindow).Select(p => p.Key).ToList())
        {
            _lastAccepted.Remove(stale);
        }
    }

    private sealed class Aggregate
    {
        private readonly HashSet<Guid> _users = [];

        public Aggregate(string anchor, string view, bool required, string? webVersion, DateTimeOffset first)
        {
            Anchor = anchor;
            View = view;
            Required = required;
            WebVersion = webVersion;
            FirstUtc = first;
            LastUtc = first;
            Client = string.Empty;
            ClientVersion = string.Empty;
        }

        public string Anchor { get; }

        public string View { get; }

        public bool Required { get; }

        public string? WebVersion { get; }

        public string Client { get; private set; }

        public string ClientVersion { get; private set; }

        public ClientLayout Layout { get; private set; }

        public bool Experimental { get; private set; }

        public string? PluginVersion { get; private set; }

        public long Count { get; private set; }

        public long Reports { get; private set; }

        public DateTimeOffset FirstUtc { get; }

        public DateTimeOffset LastUtc { get; private set; }

        // The registry's update counter at the last report: the order entries make room in, independent of the clock.
        public long LastUpdate { get; private set; }

        public void Add(Guid userId, ClientHealthReport report, int count, DateTimeOffset now, long update)
        {
            LastUpdate = update;
            if (_users.Count < MaxUsersPerEntry)
            {
                _users.Add(userId);
            }

            Client = report.Client;
            ClientVersion = report.ClientVersion;
            Layout = report.Layout;
            Experimental = report.Experimental;
            PluginVersion = report.PluginVersion;
            Count += count;
            Reports++;
            LastUtc = now;
        }

        public ClientHealthEntry ToEntry()
            => new(Anchor, View, Required, WebVersion, Client, ClientVersion, Layout, Experimental, PluginVersion, Count, Reports, _users.Count, FirstUtc, LastUtc);
    }
}
