using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.AccessRoles.Policy;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Users;
using Moq;

namespace Jellyfin.Plugin.AccessRoles.Tests.TestSupport;

/// <summary>
/// Jellyfin 10.11.5's user manager as far as policies go, for the <c>PolicyWriter</c> round trip (the sandbox measurement's approach
/// without a server): users are the real <see cref="User"/> entities, a "database" copy and the user manager's cache
/// apart; <c>GetUserDto</c> builds the policy exactly as
/// <c>v10.11.5:Jellyfin.Server.Implementations/Users/UserManager.cs:344-389</c> does (so <c>EnableLyricManagement</c> is
/// never filled), and <c>UpdatePolicyAsync</c> loads a fresh entity from the "database", sets every field as L647-721 do
/// (the lyric permission from the policy, the schedules replaced, no lock), puts it into the cache, and saves it unless
/// another save committed since its load (the row version is the concurrency token: then it throws as EF Core's
/// <c>DbUpdateConcurrencyException</c> does, and the cache keeps the entity, as L719-720 leave it). <see cref="Committed"/>
/// reads the "database" as the writer's read-only <c>IDbContextFactory</c> read does. <see cref="Dump"/>
/// reads every stored field as the sandbox's dumper did: the user-row columns (never the password), all 24 permissions, all 13
/// preferences and the access schedules.
/// </summary>
internal sealed class FakeJellyfinUsers : ICommittedUsers
{
    private readonly Dictionary<Guid, User> _database = [];
    private readonly Dictionary<Guid, User> _cache = [];
    private readonly Dictionary<Guid, User> _previous = [];

    public FakeJellyfinUsers()
    {
        Manager = new Mock<IUserManager>(MockBehavior.Loose);
        Manager.SetupGet(m => m.Users).Returns(() => _cache.Values.ToList());
        Manager.Setup(m => m.GetUserById(It.IsAny<Guid>())).Returns((Guid id) =>
        {
            GetCalls++;
            BeforeGetUserById?.Invoke(id, GetCalls);
            return _cache.GetValueOrDefault(id);
        });
        Manager.Setup(m => m.GetUserDto(It.IsAny<User>(), It.IsAny<string?>())).Returns((User user, string? _) => new UserDto { Id = user.Id, Name = user.Username, Policy = PolicyOf(user) });
        Manager.Setup(m => m.UpdatePolicyAsync(It.IsAny<Guid>(), It.IsAny<UserPolicy>())).Returns((Guid id, UserPolicy policy) =>
        {
            if (FailWrites)
            {
                throw new InvalidOperationException("The fake database refused the write.");
            }

            // 10.11.5 loads the row, sets every field, puts the entity into its cache, then saves (UserManager.cs:719-720): a
            // save that another committed before fails, and leaves the cache ahead of the database.
            var loaded = _database[id].RowVersion;
            var user = Apply(id, policy, save: false);
            DuringSave?.Invoke(id);
            if (FailingSaves > 0 || _database[id].RowVersion != loaded)
            {
                // A concurrent sign-in saved the row first (FailingSaves), or another policy save committed meanwhile.
                FailingSaves = Math.Max(0, FailingSaves - 1);
                throw new InvalidOperationException("The database operation was expected to affect 1 row(s), but actually affected 0 row(s).");
            }

            user.OnSavingChanges();
            _database[id] = Clone(user);
            Writes.Add((id, PolicyOf(user)));
            return Task.CompletedTask;
        });
    }

    /// <summary>Gets the user manager.</summary>
    public Mock<IUserManager> Manager { get; }

    /// <summary>Gets every <c>UpdatePolicyAsync</c> that reached the fake, with the policy stored afterwards.</summary>
    public List<(Guid UserId, UserPolicy Stored)> Writes { get; } = [];

    /// <summary>Gets how many times <c>GetUserById</c> was called.</summary>
    public int GetCalls { get; private set; }

    /// <summary>Gets or sets a hook run before each <c>GetUserById</c> (the user id and the call's number): a concurrent edit.</summary>
    public Action<Guid, int>? BeforeGetUserById { get; set; }

    /// <summary>Gets or sets a value indicating whether <c>UpdatePolicyAsync</c> throws.</summary>
    public bool FailWrites { get; set; }

    /// <summary>Gets or sets how many next <c>UpdatePolicyAsync</c> calls fail at their save, as the core's do: cache set, database not.</summary>
    public int FailingSaves { get; set; }

    /// <summary>
    /// Gets or sets a hook run inside <c>UpdatePolicyAsync</c> after the entity went into the cache and before its save (the
    /// user id): another save that commits there makes this one fail (<see cref="AdminCommit"/>).
    /// </summary>
    public Action<Guid>? DuringSave { get; set; }

    /// <summary>Gets or sets a value indicating whether the committed-row read fails (the database can't be read).</summary>
    public bool CommittedUnavailable { get; set; }

    /// <summary>Gets or sets a hook run before each committed-row read (the user id).</summary>
    public Action<Guid>? BeforeCommittedRead { get; set; }

    /// <summary>Gets how many committed-row reads were made.</summary>
    public int CommittedReads { get; private set; }

    /// <summary>Gets the committed-row reader over the "database" (the writer's read-only database read).</summary>
    public ICommittedUsers Committed => this;

    /// <summary>
    /// Builds a policy from a user exactly as <c>GetUserDto</c> does on 10.11.5 (<c>UserManager.cs:344-389</c>).
    /// </summary>
    /// <param name="user">The user.</param>
    /// <returns>The policy.</returns>
    public static UserPolicy PolicyOf(User user) => new()
    {
        MaxParentalRating = user.MaxParentalRatingScore,
        MaxParentalSubRating = user.MaxParentalRatingSubScore,
        EnableUserPreferenceAccess = user.EnableUserPreferenceAccess,
        RemoteClientBitrateLimit = user.RemoteClientBitrateLimit ?? 0,
        AuthenticationProviderId = user.AuthenticationProviderId,
        PasswordResetProviderId = user.PasswordResetProviderId,
        InvalidLoginAttemptCount = user.InvalidLoginAttemptCount,
        LoginAttemptsBeforeLockout = user.LoginAttemptsBeforeLockout ?? -1,
        MaxActiveSessions = user.MaxActiveSessions,
        IsAdministrator = user.HasPermission(PermissionKind.IsAdministrator),
        IsHidden = user.HasPermission(PermissionKind.IsHidden),
        IsDisabled = user.HasPermission(PermissionKind.IsDisabled),
        EnableSharedDeviceControl = user.HasPermission(PermissionKind.EnableSharedDeviceControl),
        EnableRemoteAccess = user.HasPermission(PermissionKind.EnableRemoteAccess),
        EnableLiveTvManagement = user.HasPermission(PermissionKind.EnableLiveTvManagement),
        EnableLiveTvAccess = user.HasPermission(PermissionKind.EnableLiveTvAccess),
        EnableMediaPlayback = user.HasPermission(PermissionKind.EnableMediaPlayback),
        EnableAudioPlaybackTranscoding = user.HasPermission(PermissionKind.EnableAudioPlaybackTranscoding),
        EnableVideoPlaybackTranscoding = user.HasPermission(PermissionKind.EnableVideoPlaybackTranscoding),
        EnableContentDeletion = user.HasPermission(PermissionKind.EnableContentDeletion),
        EnableContentDownloading = user.HasPermission(PermissionKind.EnableContentDownloading),
        EnableSyncTranscoding = user.HasPermission(PermissionKind.EnableSyncTranscoding),
        EnableMediaConversion = user.HasPermission(PermissionKind.EnableMediaConversion),
        EnableAllChannels = user.HasPermission(PermissionKind.EnableAllChannels),
        EnableAllDevices = user.HasPermission(PermissionKind.EnableAllDevices),
        EnableAllFolders = user.HasPermission(PermissionKind.EnableAllFolders),
        EnableRemoteControlOfOtherUsers = user.HasPermission(PermissionKind.EnableRemoteControlOfOtherUsers),
        EnablePlaybackRemuxing = user.HasPermission(PermissionKind.EnablePlaybackRemuxing),
        ForceRemoteSourceTranscoding = user.HasPermission(PermissionKind.ForceRemoteSourceTranscoding),
        EnablePublicSharing = user.HasPermission(PermissionKind.EnablePublicSharing),
        EnableCollectionManagement = user.HasPermission(PermissionKind.EnableCollectionManagement),
        EnableSubtitleManagement = user.HasPermission(PermissionKind.EnableSubtitleManagement),
        AccessSchedules = user.AccessSchedules.ToArray(),
        BlockedTags = user.GetPreference(PreferenceKind.BlockedTags),
        AllowedTags = user.GetPreference(PreferenceKind.AllowedTags),
        EnabledChannels = user.GetPreferenceValues<Guid>(PreferenceKind.EnabledChannels),
        EnabledDevices = user.GetPreference(PreferenceKind.EnabledDevices),
        EnabledFolders = user.GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders),
        EnableContentDeletionFromFolders = user.GetPreference(PreferenceKind.EnableContentDeletionFromFolders),
        SyncPlayAccess = user.SyncPlayAccess,
        BlockedChannels = user.GetPreferenceValues<Guid>(PreferenceKind.BlockedChannels),
        BlockedMediaFolders = user.GetPreferenceValues<Guid>(PreferenceKind.BlockedMediaFolders),
        BlockUnratedItems = user.GetPreferenceValues<UnratedItem>(PreferenceKind.BlockUnratedItems)
    };

    /// <summary>
    /// Reads every stored field of a user, as the sandbox's dumper did: the user-row columns (not the password), every
    /// permission, every preference, and the access schedules with their row ids.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <returns>The fields by name.</returns>
    public static SortedDictionary<string, string> Dump(User user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var fields = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in Columns())
        {
            fields["User." + property.Name] = Convert.ToString(property.GetValue(user), CultureInfo.InvariantCulture) ?? "null";
        }

        foreach (var kind in Enum.GetValues<PermissionKind>())
        {
            fields["Permission." + kind] = user.Permissions.FirstOrDefault(p => p.Kind == kind)?.Value.ToString(CultureInfo.InvariantCulture) ?? "(no row)";
        }

        foreach (var kind in Enum.GetValues<PreferenceKind>())
        {
            fields["Preference." + kind] = user.Preferences.FirstOrDefault(p => p.Kind == kind)?.Value ?? "(no row)";
        }

        fields["AccessSchedules"] = string.Join(";", user.AccessSchedules.OrderBy(s => s.Id).Select(s => string.Format(CultureInfo.InvariantCulture, "{0} {1}-{2}", s.DayOfWeek, s.StartHour, s.EndHour)));
        fields["AccessScheduleIds"] = string.Join(",", user.AccessSchedules.Select(s => s.Id).Order());
        return fields;
    }

    /// <summary>
    /// Lists the fields that differ between two dumps.
    /// </summary>
    /// <param name="before">One dump.</param>
    /// <param name="after">The other.</param>
    /// <returns>The differing fields.</returns>
    public static IReadOnlyList<string> Differences(IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> after)
        => [.. before.Keys.Union(after.Keys).Where(k => !before.TryGetValue(k, out var a) || !after.TryGetValue(k, out var b) || a != b).Order(StringComparer.Ordinal)];

    /// <summary>
    /// Adds a user with Jellyfin's default permissions and preferences (as <c>CreateUserAsync</c> gives them), then the
    /// library access given.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <param name="admin">Whether the user is an administrator.</param>
    /// <param name="allFolders">Whether every library is granted.</param>
    /// <param name="folders">The libraries otherwise.</param>
    /// <param name="disabled">Whether the account is disabled.</param>
    /// <returns>The user (the cache's object).</returns>
    public User Add(string name, bool admin = false, bool allFolders = true, IEnumerable<Guid>? folders = null, bool disabled = false)
    {
        var user = new User(name, "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider", "Jellyfin.Server.Implementations.Users.DefaultPasswordResetProvider");
        user.AddDefaultPermissions();
        user.AddDefaultPreferences();
        user.SetPermission(PermissionKind.IsAdministrator, admin);
        user.SetPermission(PermissionKind.IsDisabled, disabled);
        user.SetPermission(PermissionKind.EnableAllFolders, allFolders);
        user.SetPreference(PreferenceKind.EnabledFolders, (folders ?? []).ToArray());
        Put(user);
        return _cache[user.Id];
    }

    /// <summary>
    /// Adds the sandbox measurement's round-trip user <c>rt</c>: a deliberately non-default policy (an access schedule, tags, a rating
    /// limit, device and deletion lists, a bitrate limit, <c>JoinGroups</c>, lockout 5, sessions 3, the lyric permission on),
    /// and <c>BlockedMediaFolders</c>/<c>BlockedChannels</c> rows written straight into the "database", which the API can't.
    /// </summary>
    /// <param name="folder">The one library it may open.</param>
    /// <returns>The user.</returns>
    public User AddRich(Guid folder)
    {
        var user = new User("rt", "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider", "Jellyfin.Server.Implementations.Users.DefaultPasswordResetProvider");
        user.AddDefaultPermissions();
        user.AddDefaultPreferences();
        user.SetPermission(PermissionKind.IsHidden, false);
        user.SetPermission(PermissionKind.EnableCollectionManagement, true);
        user.SetPermission(PermissionKind.EnableSubtitleManagement, true);
        user.SetPermission(PermissionKind.EnableLyricManagement, true);
        user.SetPermission(PermissionKind.EnableRemoteControlOfOtherUsers, true);
        user.SetPermission(PermissionKind.EnableSharedDeviceControl, false);
        user.SetPermission(PermissionKind.EnableLiveTvAccess, false);
        user.SetPermission(PermissionKind.EnableLiveTvManagement, false);
        user.SetPermission(PermissionKind.EnableAudioPlaybackTranscoding, false);
        user.SetPermission(PermissionKind.EnableVideoPlaybackTranscoding, false);
        user.SetPermission(PermissionKind.EnablePlaybackRemuxing, false);
        user.SetPermission(PermissionKind.ForceRemoteSourceTranscoding, true);
        user.SetPermission(PermissionKind.EnableContentDeletion, true);
        user.SetPermission(PermissionKind.EnableContentDownloading, false);
        user.SetPermission(PermissionKind.EnableSyncTranscoding, false);
        user.SetPermission(PermissionKind.EnableMediaConversion, false);
        user.SetPermission(PermissionKind.EnableAllDevices, false);
        user.SetPermission(PermissionKind.EnableAllChannels, false);
        user.SetPermission(PermissionKind.EnableAllFolders, false);
        user.SetPermission(PermissionKind.EnablePublicSharing, false);
        user.MaxParentalRatingScore = 13;
        user.EnableUserPreferenceAccess = false;
        user.LoginAttemptsBeforeLockout = 5;
        user.MaxActiveSessions = 3;
        user.RemoteClientBitrateLimit = 8_000_000;
        user.SyncPlayAccess = SyncPlayUserAccessType.JoinGroups;
        user.SetPreference(PreferenceKind.BlockedTags, ["roundtrip-blocked"]);
        user.SetPreference(PreferenceKind.AllowedTags, ["roundtrip-allowed"]);
        user.SetPreference(PreferenceKind.BlockUnratedItems, [UnratedItem.Movie]);
        user.SetPreference(PreferenceKind.EnableContentDeletionFromFolders, ["Shows"]);
        user.SetPreference(PreferenceKind.EnabledDevices, ["roundtrip-device"]);
        user.SetPreference(PreferenceKind.EnabledFolders, [folder]);
        user.SetPreference(PreferenceKind.BlockedMediaFolders, [Guid.Parse("0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f")]);
        user.SetPreference(PreferenceKind.BlockedChannels, [Guid.Parse("1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e")]);
        var schedule = new AccessSchedule(DynamicDayOfWeek.Monday, 8, 20, user.Id);
        typeof(AccessSchedule).GetProperty(nameof(AccessSchedule.Id))!.SetValue(schedule, 13);
        user.AccessSchedules.Add(schedule);
        Put(user);
        return _cache[user.Id];
    }

    /// <summary>Gets the "database" copy of a user, the one <see cref="Dump"/> reads for the stored fields.</summary>
    /// <param name="userId">The user.</param>
    /// <returns>The user.</returns>
    public User Stored(Guid userId) => _database[userId];

    /// <summary>Gets the user manager's cached object: what the server enforces.</summary>
    /// <param name="userId">The user.</param>
    /// <returns>The user.</returns>
    public User Cached(Guid userId) => _cache[userId];

    /// <summary>
    /// An admin's full-policy post (<c>GET /Users/{id}</c> then <c>POST /Users/{id}/Policy</c>, which the core turns into
    /// <c>UpdatePolicyAsync</c>), with a change, without going through the writer: an edit "outside Access Roles".
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="change">The change to the policy.</param>
    public void AdminPost(Guid userId, Action<UserPolicy> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var policy = PolicyOf(_cache[userId]);
        policy.EnableLyricManagement = _cache[userId].HasPermission(PermissionKind.EnableLyricManagement);
        change(policy);
        Apply(userId, policy);
    }

    /// <summary>
    /// An admin's Dashboard save that commits to the "database" without touching the cache: its <c>UpdatePolicyAsync</c>
    /// put its entity into the cache before ours did, and its save lands while ours is on its way (the race:
    /// ours then fails, and the cache holds our entity). The policy is the one the admin's page loaded, with the change.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="loaded">The policy the admin's page loaded (<c>GET /Users/{id}</c>), or <c>null</c> for the cache's now.</param>
    /// <param name="change">The admin's change.</param>
    public void AdminCommit(Guid userId, UserPolicy? loaded, Action<UserPolicy> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var policy = loaded ?? PolicyOf(_cache[userId]);
        policy.EnableLyricManagement = _database[userId].HasPermission(PermissionKind.EnableLyricManagement);
        change(policy);
        var user = Build(userId, policy);
        user.OnSavingChanges();
        _database[userId] = user;
    }

    /// <summary>
    /// The policy an admin's page loads: <c>GET /Users/{id}</c>'s, from the cache.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <returns>The policy, with the lyric permission as the page shows it.</returns>
    public UserPolicy Load(Guid userId)
    {
        var policy = PolicyOf(_cache[userId]);
        policy.EnableLyricManagement = _cache[userId].HasPermission(PermissionKind.EnableLyricManagement);
        return policy;
    }

    /// <summary>
    /// Puts a stale object into the cache: the sign-in race after an admin's save, so the server enforces what
    /// the database no longer holds; or a lockout 10.11.5 never saves (jellyfin/jellyfin#16298).
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="change">The change of the cached object only.</param>
    public void ChangeCacheOnly(Guid userId, Action<User> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var stale = Clone(_cache[userId]);
        change(stale);
        _cache[userId] = stale;
    }

    /// <inheritdoc />
    Task<CommittedUser> ICommittedUsers.ReadAsync(Guid userId, CancellationToken cancellationToken)
    {
        CommittedReads++;
        BeforeCommittedRead?.Invoke(userId);
        if (CommittedUnavailable)
        {
            return Task.FromResult(CommittedUser.Unavailable);
        }

        return Task.FromResult(_database.TryGetValue(userId, out var user) ? new CommittedUser(true, Clone(user)) : CommittedUser.Missing);
    }

    /// <summary>
    /// The 10.11.5 sign-in race: a sign-in that took the cached user before the last write puts that
    /// object back into the cache after it, so the server enforces the old access while the database holds the new.
    /// </summary>
    /// <param name="userId">The user.</param>
    public void SignInRace(Guid userId) => _cache[userId] = _previous[userId];

    /// <summary>Gets the row version of a user's stored row (where the core's own entity of a new user stands).</summary>
    /// <param name="userId">The user.</param>
    /// <returns>The row version.</returns>
    public uint RowVersion(Guid userId) => _database[userId].RowVersion;

    /// <summary>
    /// <c>CreateUserByName</c>'s own save of a new user's password after <c>UserCreatedEventArgs</c>: <c>UpdateUserAsync</c>
    /// on the entity it created (<c>v10.11.5:Jellyfin.Api/Controllers/UserController.cs:543-549</c>), which fails as the
    /// core's does when another save moved the row on since (HTTP 500, the user left without a password).
    /// </summary>
    /// <param name="userId">The new user.</param>
    /// <param name="createdAt">The row version the core's entity has (<see cref="RowVersion"/> right after the creation).</param>
    public void SaveCreatedUsersPassword(Guid userId, uint createdAt)
    {
        if (_database[userId].RowVersion != createdAt)
        {
            throw new InvalidOperationException("The database operation was expected to affect 1 row(s), but actually affected 0 row(s).");
        }

        var user = Clone(_database[userId]);
        user.OnSavingChanges();
        _database[userId] = user;
        _cache[userId] = Clone(user);
    }

    /// <summary>Removes a user (deleted on the server).</summary>
    /// <param name="userId">The user.</param>
    public void Delete(Guid userId)
    {
        _cache.Remove(userId);
        _database.Remove(userId);
    }

    private static IEnumerable<PropertyInfo> Columns()
        => typeof(User).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.Name != nameof(User.Password) && p.GetIndexParameters().Length == 0)
            .Where(p => p.PropertyType.IsValueType || p.PropertyType == typeof(string))
            .OrderBy(p => p.Name, StringComparer.Ordinal);

    private static User Clone(User user)
    {
        var copy = new User(user.Username, user.AuthenticationProviderId, user.PasswordResetProviderId);
        foreach (var property in Columns().Concat([typeof(User).GetProperty(nameof(User.Password))!]))
        {
            property.GetSetMethod(nonPublic: true)?.Invoke(copy, [property.GetValue(user)]);
        }

        foreach (var permission in user.Permissions)
        {
            copy.Permissions.Add(new Permission(permission.Kind, permission.Value));
        }

        foreach (var preference in user.Preferences)
        {
            copy.Preferences.Add(new Preference(preference.Kind, preference.Value));
        }

        foreach (var schedule in user.AccessSchedules)
        {
            var copied = new AccessSchedule(schedule.DayOfWeek, schedule.StartHour, schedule.EndHour, user.Id);
            typeof(AccessSchedule).GetProperty(nameof(AccessSchedule.Id))!.SetValue(copied, schedule.Id);
            copy.AccessSchedules.Add(copied);
        }

        return copy;
    }

    private void Put(User user)
    {
        _database[user.Id] = Clone(user);
        _cache[user.Id] = Clone(user);
        _previous[user.Id] = _cache[user.Id];
    }

    // UpdatePolicyAsync on 10.11.5 (UserManager.cs:647-721): a fresh entity from the database, every field set, the schedules
    // replaced, saved (the row version moves on, new schedule rows get new ids), and that entity put into the cache.
    private User Apply(Guid userId, UserPolicy policy, bool save = true)
    {
        var user = Build(userId, policy);
        _previous[userId] = _cache[userId];
        _cache[userId] = user;
        if (save)
        {
            user.OnSavingChanges();
            _database[userId] = Clone(user);
        }

        return user;
    }

    // A fresh entity from the "database" with every field of the policy set, as UpdatePolicyAsync builds it.
    private User Build(Guid userId, UserPolicy policy)
    {
        var user = Clone(_database[userId]);
        int? maxLoginAttempts = policy.LoginAttemptsBeforeLockout switch
        {
            -1 => null,
            0 => 3,
            _ => policy.LoginAttemptsBeforeLockout
        };

        user.MaxParentalRatingScore = policy.MaxParentalRating;
        user.MaxParentalRatingSubScore = policy.MaxParentalSubRating;
        user.EnableUserPreferenceAccess = policy.EnableUserPreferenceAccess;
        user.RemoteClientBitrateLimit = policy.RemoteClientBitrateLimit;
        user.AuthenticationProviderId = policy.AuthenticationProviderId;
        user.PasswordResetProviderId = policy.PasswordResetProviderId;
        user.InvalidLoginAttemptCount = policy.InvalidLoginAttemptCount;
        user.LoginAttemptsBeforeLockout = maxLoginAttempts;
        user.MaxActiveSessions = policy.MaxActiveSessions;
        user.SyncPlayAccess = policy.SyncPlayAccess;
        user.SetPermission(PermissionKind.IsAdministrator, policy.IsAdministrator);
        user.SetPermission(PermissionKind.IsHidden, policy.IsHidden);
        user.SetPermission(PermissionKind.IsDisabled, policy.IsDisabled);
        user.SetPermission(PermissionKind.EnableSharedDeviceControl, policy.EnableSharedDeviceControl);
        user.SetPermission(PermissionKind.EnableRemoteAccess, policy.EnableRemoteAccess);
        user.SetPermission(PermissionKind.EnableLiveTvManagement, policy.EnableLiveTvManagement);
        user.SetPermission(PermissionKind.EnableLiveTvAccess, policy.EnableLiveTvAccess);
        user.SetPermission(PermissionKind.EnableMediaPlayback, policy.EnableMediaPlayback);
        user.SetPermission(PermissionKind.EnableAudioPlaybackTranscoding, policy.EnableAudioPlaybackTranscoding);
        user.SetPermission(PermissionKind.EnableVideoPlaybackTranscoding, policy.EnableVideoPlaybackTranscoding);
        user.SetPermission(PermissionKind.EnableContentDeletion, policy.EnableContentDeletion);
        user.SetPermission(PermissionKind.EnableContentDownloading, policy.EnableContentDownloading);
        user.SetPermission(PermissionKind.EnableSyncTranscoding, policy.EnableSyncTranscoding);
        user.SetPermission(PermissionKind.EnableMediaConversion, policy.EnableMediaConversion);
        user.SetPermission(PermissionKind.EnableAllChannels, policy.EnableAllChannels);
        user.SetPermission(PermissionKind.EnableAllDevices, policy.EnableAllDevices);
        user.SetPermission(PermissionKind.EnableAllFolders, policy.EnableAllFolders);
        user.SetPermission(PermissionKind.EnableRemoteControlOfOtherUsers, policy.EnableRemoteControlOfOtherUsers);
        user.SetPermission(PermissionKind.EnablePlaybackRemuxing, policy.EnablePlaybackRemuxing);
        user.SetPermission(PermissionKind.EnableCollectionManagement, policy.EnableCollectionManagement);
        user.SetPermission(PermissionKind.EnableSubtitleManagement, policy.EnableSubtitleManagement);
        user.SetPermission(PermissionKind.EnableLyricManagement, policy.EnableLyricManagement);
        user.SetPermission(PermissionKind.ForceRemoteSourceTranscoding, policy.ForceRemoteSourceTranscoding);
        user.SetPermission(PermissionKind.EnablePublicSharing, policy.EnablePublicSharing);

        user.AccessSchedules.Clear();
        foreach (var schedule in policy.AccessSchedules)
        {
            user.AccessSchedules.Add(schedule);
        }

        user.SetPreference(PreferenceKind.BlockUnratedItems, policy.BlockUnratedItems ?? Array.Empty<UnratedItem>());
        user.SetPreference(PreferenceKind.BlockedTags, policy.BlockedTags);
        user.SetPreference(PreferenceKind.AllowedTags, policy.AllowedTags);
        user.SetPreference(PreferenceKind.EnabledChannels, policy.EnabledChannels);
        user.SetPreference(PreferenceKind.EnabledDevices, policy.EnabledDevices);
        user.SetPreference(PreferenceKind.EnabledFolders, policy.EnabledFolders);
        user.SetPreference(PreferenceKind.EnableContentDeletionFromFolders, policy.EnableContentDeletionFromFolders);
        return user;
    }
}
