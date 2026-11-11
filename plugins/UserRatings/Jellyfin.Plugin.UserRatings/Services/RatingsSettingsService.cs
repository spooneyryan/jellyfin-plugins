using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Users;
using Jellyfin.Plugin.UserRatings.Api.Models;
using Jellyfin.Plugin.UserRatings.Configuration;
using Jellyfin.Plugin.UserRatings.Lifecycle;
using Jellyfin.Plugin.UserRatings.Storage;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.UserRatings.Services;

/// <summary>
/// The Settings tab's server side: the settings with their defaults, version and what the tab offers,
/// and a change, a dry run by default, checked field by field and saved through Jellyfin's own
/// <c>UpdateConfiguration</c>.
/// </summary>
/// <remarks>
/// <para><b>Validation</b> is the server's, whatever the page checked: a number out of its range, a name the setting doesn't
/// have, a user who isn't on the server, an item kind the tab doesn't offer, an empty <c>RateableItemTypes</c>,
/// <c>SelectedUsers</c> with no user of the server, and <c>MirrorToUserData</c> turned on (not allowed yet) are each a 400
/// whose <c>code</c> is <c>InvalidSetting:</c> and the setting's name (its words name it too); a key that names no setting is
/// a 400 <c>UnknownSetting</c>. Nothing is saved then, nor in a dry run.</para>
/// <para><b>Concurrency.</b> A save carries the version it was made from (400 <c>VersionNeeded</c> without), and is refused
/// with a 409 <c>SettingsChanged</c> when the settings were saved since; the 409 names the settings that changed when the
/// version is one this server answered since it started (the last 64 are remembered). One save at a time, from its check
/// to its save.</para>
/// <para><b>What a value this version doesn't know does.</b> The page sends only the settings the admin changed, so a value
/// a newer build wrote stays as it is; a change never writes one. A user id or an item kind the settings already hold may
/// stay although the server doesn't have it (the picker shows it as missing).</para>
/// <para>Every setting is read at its use, so a change takes effect at the next request or maintenance run, except the
/// three that need a restart (<see cref="SettingField.RestartNeeded"/>): the report says so.</para>
/// </remarks>
internal sealed class RatingsSettingsService : IDisposable
{
    /// <summary>The <c>code</c> of the 409 of a save over settings saved since its version.</summary>
    public const string SettingsChangedCode = "SettingsChanged";

    /// <summary>The <c>code</c> of the 400 of a body key that names no setting.</summary>
    public const string UnknownSettingCode = "UnknownSetting";

    /// <summary>The <c>code</c> of the 400 of a save without a version.</summary>
    public const string VersionNeededCode = "VersionNeeded";

    /// <summary>The start of the <c>code</c> of a 400 for a setting's value; the setting's name follows.</summary>
    public const string InvalidSettingCode = "InvalidSetting:";

    private const int RememberedVersions = 64;

    // A server-wide setting's counts are the whole server's, whoever asks: a
    // restricted admin's dry run counts the rows of libraries they can't open too, so each sentence with a count says so.
    // Counts only, never a title, a name or a text.
    private const string WholeServer = " on the whole server";

    private static readonly Dictionary<string, PropertyInfo> DtoProperties = typeof(UserRatingsSettingsDto)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanWrite && p.Name != nameof(UserRatingsSettingsDto.UnknownKeys))
        .ToDictionary(p => p.Name, StringComparer.Ordinal);

    private readonly IPluginConfigurationProvider _configuration;
    private readonly IUserManager _users;
    private readonly IRatingsStore _store;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _saves = new(1, 1);
    private readonly object _seenLock = new();
    private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _seen = new(StringComparer.Ordinal);
    private readonly Queue<string> _seenOrder = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="RatingsSettingsService"/> class.
    /// </summary>
    /// <param name="configuration">The settings.</param>
    /// <param name="users">The server's users: the picker, and who can rate after a change.</param>
    /// <param name="store">The ratings, for what a change hides or a lower limit means.</param>
    /// <param name="clock">The clock.</param>
    /// <param name="logger">The logger.</param>
    public RatingsSettingsService(IPluginConfigurationProvider configuration, IUserManager users, IRatingsStore store, TimeProvider clock, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _configuration = configuration;
        _users = users;
        _store = store;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Gets the settings as the API gives them, every field set.
    /// </summary>
    /// <param name="configuration">The settings.</param>
    /// <returns>The values.</returns>
    public static UserRatingsSettingsDto ToDto(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var dto = new UserRatingsSettingsDto();
        foreach (var field in RatingsSettingsFields.Fields)
        {
            DtoProperties[field.Name].SetValue(dto, RatingsSettingsFields.Get(configuration, field.Name) switch
            {
                Guid[] ids => (IReadOnlyList<Guid>)[.. ids],
                string[] names => (IReadOnlyList<string>)[.. names],
                null when RatingsSettingsFields.TypeOf(field.Name) == typeof(string) => string.Empty,
                null when RatingsSettingsFields.TypeOf(field.Name) == typeof(Guid[]) => (IReadOnlyList<Guid>)[],
                null when RatingsSettingsFields.TypeOf(field.Name) == typeof(string[]) => (IReadOnlyList<string>)[],
                var value => value
            });
        }

        return dto;
    }

    /// <summary>
    /// Reads the settings for the Settings tab (<c>GET /UserRatings/Admin/Settings</c>).
    /// </summary>
    /// <returns>The settings, their defaults and version, the users and kinds offered, and the values the plugin can't use.</returns>
    public UserRatingsSettingsViewDto Read()
    {
        var configuration = _configuration.GetConfiguration();
        var users = ServerUsers();
        var offered = users
            .OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase)
            .Select(u => new UserRatingsSettingsUserDto { Id = u.Id, Name = u.Name })
            .ToList();
        offered.AddRange((configuration.RaterUserIds ?? []).Where(id => id != Guid.Empty && !users.Any(u => u.Id == id)).Distinct()
            .Select(id => new UserRatingsSettingsUserDto { Id = id, Missing = true }));

        var kinds = RatingsSettingsFields.OfferedItemTypes.ToList();
        kinds.AddRange((configuration.RateableItemTypes ?? [])
            .Where(k => !string.IsNullOrWhiteSpace(k) && !kinds.Contains(k.Trim(), StringComparer.OrdinalIgnoreCase))
            .Select(k => k.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase));

        var problems = RatingsSettings.From(configuration).Problems.ToList();
        if (NobodySelected(configuration, users))
        {
            problems.Add("WhoCanRate is SelectedUsers, and RaterUserIds names no user of this server: nobody can rate or review");
        }

        return new UserRatingsSettingsViewDto
        {
            Settings = ToDto(configuration),
            Defaults = ToDto(new PluginConfiguration()),
            Version = Remember(configuration),
            Users = offered,
            ItemKinds = kinds,
            RestartFields = [.. RatingsSettingsFields.Fields.Where(f => f.RestartNeeded).Select(f => f.Name)],
            Problems = problems
        };
    }

    /// <summary>
    /// Changes the settings (<c>POST /UserRatings/Admin/Settings</c>): a dry run unless it says otherwise, which answers
    /// what the change changes and means; a save, checked against the version it was made from.
    /// </summary>
    /// <param name="change">The settings to change; the ones left out keep their values.</param>
    /// <param name="version">The version the change was made from: a save needs it; a dry run with it is checked too.</param>
    /// <param name="dryRun">Whether nothing is saved; true when null.</param>
    /// <param name="cancellationToken">Cancels waiting for another save.</param>
    /// <returns>The report.</returns>
    /// <exception cref="PluginException">400 a key that names no setting, a value it can't use, a save without a version;
    /// 409 <c>SettingsChanged</c>; 503 the plugin isn't loaded (a save).</exception>
    public async Task<UserRatingsSettingsReportDto> ChangeAsync(UserRatingsSettingsDto? change, string? version, bool? dryRun, CancellationToken cancellationToken)
    {
        if (change is null)
        {
            throw PluginException.Invalid("The body must be the settings to change, as GET /UserRatings/Admin/Settings answers them in Settings.");
        }

        if (change.UnknownKeys is { Count: > 0 } unknown)
        {
            var names = unknown.Keys.Order(StringComparer.Ordinal).Take(10).Select(k => k.Length > 64 ? k[..64] + "…" : k);
            throw new PluginException(
                PluginErrorKind.Invalid,
                "No such setting: " + string.Join(", ", names) + (unknown.Count > 10 ? " and " + (unknown.Count - 10).ToString(CultureInfo.InvariantCulture) + " more" : string.Empty) + " (the names are as GET Admin/Settings answers them). Nothing was saved.")
            { Code = UnknownSettingCode };
        }

        var basedOn = version?.Trim();
        if (!string.IsNullOrEmpty(basedOn) && (basedOn.Length != 16 || !basedOn.All(char.IsAsciiHexDigit)))
        {
            throw new PluginException(PluginErrorKind.Invalid, "version must be the Version GET /UserRatings/Admin/Settings answered (16 hex digits).") { Code = VersionNeededCode };
        }

        var dry = dryRun ?? true;
        if (!dry && string.IsNullOrEmpty(basedOn))
        {
            throw new PluginException(PluginErrorKind.Invalid, "A save needs the version of the settings it was made from: read them (GET /UserRatings/Admin/Settings) and send their Version as version.") { Code = VersionNeededCode };
        }

        if (dry)
        {
            return Change(change, basedOn, dry: true);
        }

        await _saves.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return Change(change, basedOn, dry: false);
        }
        finally
        {
            _saves.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _saves.Dispose();

    // A 400 for one setting's value: its name is the code's end and the words' start.
    private static PluginException Invalid(string field, string message)
        => new(PluginErrorKind.Invalid, field + " " + message + " Nothing was saved.") { Code = InvalidSettingCode + field };

    private static bool NobodySelected(PluginConfiguration configuration, IReadOnlyList<(Guid Id, string Name)> users)
        => RatingsSettings.From(configuration).WhoCanRate == WhoCanRate.SelectedUsers
            && !(configuration.RaterUserIds ?? []).Any(id => users.Any(u => u.Id == id));

    private static string Count(int count, string one, string many)
        => count.ToString("N0", CultureInfo.InvariantCulture) + " " + (count == 1 ? one : many);

    private static string OneOf(SettingField field, string text)
    {
        var names = field.Names ?? throw Invalid(field.Name, "has a value of the wrong type.");
        return names.FirstOrDefault(n => string.Equals(n, text.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw Invalid(field.Name, "must be " + string.Join(", ", names.SkipLast(1)) + " or " + names[^1] + ".");
    }

    // The users who may rate: users of the server, or ids the settings already hold; in the server's order by name, then
    // the held ones that are no longer users, as they were.
    private static Guid[] Raters(IReadOnlyList<Guid> ids, PluginConfiguration current, IReadOnlyList<(Guid Id, string Name)> users)
    {
        const string Field = nameof(PluginConfiguration.RaterUserIds);
        var held = (current.RaterUserIds ?? []).ToHashSet();
        var wanted = new HashSet<Guid>();
        foreach (var id in ids)
        {
            if (id == Guid.Empty)
            {
                throw Invalid(Field, "can't hold an empty id.");
            }

            if (!held.Contains(id) && !users.Any(u => u.Id == id))
            {
                throw Invalid(Field, "can name only users of this server: " + id.ToString("N", CultureInfo.InvariantCulture) + " isn't one (deleted since the page was opened?). Reload the page.");
            }

            wanted.Add(id);
        }

        return
        [
            .. users.OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase).Select(u => u.Id).Where(wanted.Contains),
            .. (current.RaterUserIds ?? []).Where(id => wanted.Contains(id) && !users.Any(u => u.Id == id)).Distinct()
        ];
    }

    // The rateable kinds: the offered ones in their spelling, or names the settings already hold; at least one; in the
    // offered order, then the held ones.
    private static string[] Kinds(IReadOnlyList<string> kinds, PluginConfiguration current)
    {
        const string Field = nameof(PluginConfiguration.RateableItemTypes);
        var held = (current.RateableItemTypes ?? []).Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k.Trim()).ToList();
        var wanted = new List<string>();
        foreach (var kind in kinds)
        {
            var text = kind?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                throw Invalid(Field, "can't hold an empty name.");
            }

            var known = RatingsSettingsFields.OfferedItemTypes.FirstOrDefault(k => string.Equals(k, text, StringComparison.OrdinalIgnoreCase))
                ?? held.FirstOrDefault(k => string.Equals(k, text, StringComparison.OrdinalIgnoreCase))
                ?? throw Invalid(Field, "can name only " + string.Join(", ", RatingsSettingsFields.OfferedItemTypes) + ": '" + (text.Length > 64 ? text[..64] + "…" : text) + "' isn't one.");
            if (!wanted.Contains(known, StringComparer.OrdinalIgnoreCase))
            {
                wanted.Add(known);
            }
        }

        if (wanted.Count == 0)
        {
            throw Invalid(Field, "must name at least one item kind: with none, nothing could be rated.");
        }

        return
        [
            .. RatingsSettingsFields.OfferedItemTypes.Where(k => wanted.Contains(k, StringComparer.Ordinal)),
            .. wanted.Where(k => !RatingsSettingsFields.OfferedItemTypes.Contains(k, StringComparer.Ordinal))
        ];
    }

    // Through the kit: 10.11.9 replaced IUserManager.Users with GetUsers().
    private List<(Guid Id, string Name)> ServerUsers()
        => [.. KitUsers.GetAll(_users).Where(u => u is not null).Select(u => (u.Id, u.Username ?? string.Empty))];

    private UserRatingsSettingsReportDto Change(UserRatingsSettingsDto change, string? basedOn, bool dry)
    {
        var current = _configuration.GetConfiguration();
        var lines = RatingsSettingsFields.Lines(current);
        var version = RatingsSettingsFields.Version(lines);
        Remember(version, lines);
        if (!string.IsNullOrEmpty(basedOn) && !string.Equals(basedOn, version, StringComparison.OrdinalIgnoreCase))
        {
            throw new PluginException(PluginErrorKind.Conflict, ChangedSince(basedOn, lines)) { Code = SettingsChangedCode };
        }

        var users = ServerUsers();
        var proposed = Apply(current, change, users);
        var name = users.ToDictionary(u => u.Id, u => u.Name);
        var changes = RatingsSettingsFields.Fields
            .Where(f => !string.Equals(lines[f.Name], RatingsSettingsFields.Canonical(proposed, f.Name), StringComparison.Ordinal))
            .Select(f => new UserRatingsSettingsChangeDto
            {
                Field = f.Name,
                Label = f.Label,
                From = RatingsSettingsFields.Text(current, f.Name, id => name.TryGetValue(id, out var n) ? n : null),
                To = RatingsSettingsFields.Text(proposed, f.Name, id => name.TryGetValue(id, out var n) ? n : null),
                RestartNeeded = f.RestartNeeded
            })
            .ToList();
        var report = new UserRatingsSettingsReportDto
        {
            DryRun = dry,
            Version = version,
            Settings = ToDto(proposed),
            Changes = changes,
            Consequences = Consequences(current, proposed, users),
            RestartNeeded = changes.Any(c => c.RestartNeeded)
        };
        if (!dry && changes.Count > 0)
        {
            _configuration.Save(proposed);
            report.Applied = true;
            report.Version = Remember(_configuration.GetConfiguration());
            _logger.LogInformation("User Ratings' settings were saved: {Fields}", string.Join(", ", changes.Select(c => c.Field)));
        }

        return report;
    }

    // The 409's words: the settings saved since the caller's version, when this server answered it.
    private string ChangedSince(string basedOn, IReadOnlyDictionary<string, string> lines)
    {
        IReadOnlyDictionary<string, string>? then;
        lock (_seenLock)
        {
            _seen.TryGetValue(basedOn.ToLowerInvariant(), out then);
        }

        var changed = then is null
            ? []
            : RatingsSettingsFields.Fields.Where(f => !string.Equals(then[f.Name], lines[f.Name], StringComparison.Ordinal)).Select(f => f.Label).ToList();
        return "The settings were saved since you opened them (by someone else, or in another window)"
            + (changed.Count > 0 ? ": " + string.Join(", ", changed) : string.Empty)
            + ". Reload them, then make your change again. Nothing was saved.";
    }

    // The change over the saved settings, every value given checked; a value left out stays.
    private PluginConfiguration Apply(PluginConfiguration current, UserRatingsSettingsDto change, IReadOnlyList<(Guid Id, string Name)> users)
    {
        var next = RatingsSettingsFields.Copy(current);
        var given = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in RatingsSettingsFields.Fields)
        {
            var value = DtoProperties[field.Name].GetValue(change);
            if (value is null)
            {
                continue;
            }

            given.Add(field.Name);
            RatingsSettingsFields.Set(next, field.Name, value switch
            {
                bool on when field.Name == nameof(PluginConfiguration.MirrorToUserData) && on && !current.MirrorToUserData
                    => throw Invalid(field.Name, "can't be turned on yet: it waits for a test that shows that a rating written into Jellyfin's own user data can't reset a member's favourite, played and resume state."),
                bool on => on,
                int number when number < field.Min || number > field.Max
                    => throw Invalid(field.Name, string.Create(CultureInfo.InvariantCulture, $"must be a whole number from {field.Min} to {field.Max}.")),
                int number => number,
                string text => OneOf(field, text),
                IReadOnlyList<Guid> ids => Raters(ids, current, users),
                IReadOnlyList<string> kinds => Kinds(kinds, current),
                _ => throw Invalid(field.Name, "has a value of the wrong type.")
            });
        }

        if ((given.Contains(nameof(PluginConfiguration.WhoCanRate)) || given.Contains(nameof(PluginConfiguration.RaterUserIds))) && NobodySelected(next, users))
        {
            throw Invalid(nameof(PluginConfiguration.RaterUserIds), "must name at least one user of this server when only selected users can rate (WhoCanRate SelectedUsers): with none, nobody could rate or review.");
        }

        return next;
    }

    // What a change means for the members and the data, in sentences, with counts read now.
    private List<string> Consequences(PluginConfiguration current, PluginConfiguration proposed, IReadOnlyList<(Guid Id, string Name)> users)
    {
        var consequences = new List<string>();
        var before = RatingsSettings.From(current);
        var after = RatingsSettings.From(proposed);

        var lose = users.Count(u => before.MayRate(u.Id) && !after.MayRate(u.Id));
        var gain = users.Count(u => !before.MayRate(u.Id) && after.MayRate(u.Id));
        if (lose > 0)
        {
            consequences.Add(Count(lose, "user", "users") + " can no longer rate or review. Their ratings and reviews stay and still show, and they can still delete them.");
        }

        if (gain > 0)
        {
            consequences.Add(Count(gain, "user", "users") + " can now rate and review.");
        }

        try
        {
            Counted(consequences, current, proposed, before, after);
        }
        catch (PluginException ex) when (ex.Kind == PluginErrorKind.Unavailable)
        {
            consequences.Add("The ratings data can't be read now, so what this change hides isn't counted.");
        }

        if (!string.Equals(RatingsSettingsFields.Canonical(current, nameof(PluginConfiguration.DeletedUserPolicy)), RatingsSettingsFields.Canonical(proposed, nameof(PluginConfiguration.DeletedUserPolicy)), StringComparison.Ordinal))
        {
            consequences.Add(after.DeletedUserPolicy switch
            {
                DeletedUserPolicy.Delete => "Users deleted from now on lose their ratings and reviews (a snapshot is taken first). Users deleted before stay as they are.",
                DeletedUserPolicy.Anonymize => "The ratings and reviews of users deleted from now on are kept as a former member's. Users deleted before stay as they are.",
                _ => "The ratings and reviews of users deleted from now on are kept, and counted nowhere, until an admin acts. Users deleted before stay as they are."
            });
        }

        if (proposed.SnapshotsToKeep < current.SnapshotsToKeep)
        {
            consequences.Add(proposed.SnapshotsToKeep == 0
                ? "The daily maintenance takes no more snapshots of the ratings (the ones there are stay)."
                : string.Create(CultureInfo.InvariantCulture, $"The next daily maintenance keeps only the newest {proposed.SnapshotsToKeep} daily snapshots, and deletes older ones."));
        }

        var restart = RatingsSettingsFields.Fields
            .Where(f => f.RestartNeeded && !string.Equals(RatingsSettingsFields.Canonical(current, f.Name), RatingsSettingsFields.Canonical(proposed, f.Name), StringComparison.Ordinal))
            .Select(f => f.Label)
            .ToList();
        if (restart.Count > 0)
        {
            consequences.Add("Takes effect after the server restarts: " + string.Join(", ", restart) + ".");
        }

        return consequences;
    }

    private void Counted(List<string> consequences, PluginConfiguration current, PluginConfiguration proposed, RatingsSettings before, RatingsSettings after)
    {
        var byKind = _store.CountAttachedByKind();
        (int Ratings, int Reviews) Of(Func<string, bool> kinds)
            => (byKind.Where(p => kinds(p.Key)).Sum(p => p.Value.Ratings), byKind.Where(p => kinds(p.Key)).Sum(p => p.Value.Reviews));
        bool Rateable(RatingsSettings settings, string kind) => Enum.TryParse<BaseItemKind>(kind, ignoreCase: false, out var k) && settings.IsRateable(k);

        if (before.EnableRatings && !after.EnableRatings)
        {
            var (ratings, reviews) = Of(_ => true);
            consequences.Add("Ratings and reviews stop showing in the web client (" + Count(ratings, "rating", "ratings") + ", " + Count(reviews, "review", "reviews") + WholeServer + "). Nothing is deleted: turning ratings back on shows them again.");
        }
        else if (after.EnableRatings)
        {
            var removed = byKind.Keys.Where(k => Rateable(before, k) && !Rateable(after, k)).Order(StringComparer.Ordinal).ToList();
            var (ratings, reviews) = Of(removed.Contains);
            if (ratings > 0)
            {
                consequences.Add("The ratings on " + string.Join(", ", removed.Select(RatingsSettingsFields.KindName)) + " (" + Count(ratings, "rating", "ratings") + ", " + Count(reviews, "review", "reviews") + WholeServer + ") stop showing and can't be changed. Nothing is deleted: their authors still find them among their ratings on items they can't open, and turning the kinds back on shows them again.");
            }

            if (before.EnableReviews && !after.EnableReviews)
            {
                var (_, all) = Of(_ => true);
                consequences.Add("Review text stops showing (" + Count(all, "review", "reviews") + WholeServer + "); the stars stay. Nothing is deleted.");
            }
        }

        if (after.MaxReviewLength < before.MaxReviewLength)
        {
            var longer = _store.CountReviewsLongerThan(after.MaxReviewLength);
            if (longer > 0)
            {
                consequences.Add(longer == 1
                    ? string.Create(CultureInfo.InvariantCulture, $"1 review{WholeServer} is longer than {after.MaxReviewLength} characters: it stays as written, and must be shortened before it can be edited.")
                    : Count(longer, "review", "reviews") + string.Create(CultureInfo.InvariantCulture, $"{WholeServer} are longer than {after.MaxReviewLength} characters: they stay as written, and must be shortened before they can be edited."));
            }
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        var oldDetached = Math.Clamp(current.DetachedRetentionDays, 0, RatingsLibraryConsumer.MaxRetentionDays);
        var newDetached = proposed.DetachedRetentionDays;
        if (newDetached > 0 && (oldDetached == 0 || newDetached < oldDetached))
        {
            var due = _store.GetDetached().Count(r => r.DetachedUtc is { } at && at <= now.AddDays(-newDetached) && (oldDetached == 0 || at > now.AddDays(-oldDetached)));
            if (due > 0)
            {
                consequences.Add(Count(due, "detached rating", "detached ratings") + string.Create(CultureInfo.InvariantCulture, $"{WholeServer}, detached more than {newDetached} days ago, {(due == 1 ? "moves" : "move")} to the archive at the next daily maintenance: kept there, and still back with the item when it comes back."));
            }
        }

        var oldLog = before.ModerationLogDays;
        var newLog = after.ModerationLogDays;
        if (newLog > 0 && (oldLog == 0 || newLog < oldLog))
        {
            var rows = _store.CountModerationLogBefore(now.AddDays(-newLog)) - (oldLog == 0 ? 0 : _store.CountModerationLogBefore(now.AddDays(-oldLog)));
            if (rows > 0)
            {
                consequences.Add(Count(rows, "moderation log row", "moderation log rows") + string.Create(CultureInfo.InvariantCulture, $"{WholeServer}, older than {newLog} days, {(rows == 1 ? "is" : "are")} deleted at the next daily maintenance."));
            }
        }
    }

    private string Remember(PluginConfiguration configuration)
    {
        var lines = RatingsSettingsFields.Lines(configuration);
        var version = RatingsSettingsFields.Version(lines);
        Remember(version, lines);
        return version;
    }

    private void Remember(string version, IReadOnlyDictionary<string, string> lines)
    {
        lock (_seenLock)
        {
            if (_seen.ContainsKey(version))
            {
                return;
            }

            _seen[version] = lines;
            _seenOrder.Enqueue(version);
            while (_seenOrder.Count > RememberedVersions)
            {
                _seen.Remove(_seenOrder.Dequeue());
            }
        }
    }
}
