using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.TagCloud.Api.Models;
using Jellyfin.Plugin.TagCloud.Clouds;
using Jellyfin.Plugin.TagCloud.Configuration;
using Jellyfin.Plugin.TagCloud.Index;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.TagCloud.Services;

/// <summary>
/// Saved clouds over <see cref="SavedCloudOwner"/>'s store.
/// </summary>
internal sealed class TagCloudCloudService : ITagCloudClouds
{
    /// <summary>The longest name, in characters.</summary>
    public const int MaxNameLength = 80;

    /// <summary>The most clouds one user may keep.</summary>
    public const int MaxCloudsPerUser = 200;

    /// <summary>The most libraries one cloud counts over.</summary>
    public const int MaxLibraries = 50;

    /// <summary>The name a deleted owner shows as.</summary>
    public const string FormerUser = "Former user";

    private readonly SavedCloudOwner _owner;
    private readonly IUserManager _users;
    private readonly IPluginConfigurationProvider _configuration;
    private readonly TimeProvider _time;

    /// <summary>
    /// Initializes a new instance of the <see cref="TagCloudCloudService"/> class.
    /// </summary>
    /// <param name="owner">The clouds' store.</param>
    /// <param name="users">The user manager, for owners' names.</param>
    /// <param name="configuration">The settings (sharing).</param>
    /// <param name="time">The clock.</param>
    public TagCloudCloudService(SavedCloudOwner owner, IUserManager users, IPluginConfigurationProvider configuration, TimeProvider? time = null)
    {
        _owner = owner;
        _users = users;
        _configuration = configuration;
        _time = time ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public IReadOnlyList<TagCloudSavedCloudDto> List(User user)
    {
        ArgumentNullException.ThrowIfNull(user);
        TagCloudPageGate.Ensure(_configuration.GetConfiguration());
        var clouds = _owner.Store.Current.Clouds;
        var sharing = _configuration.GetConfiguration().AllowSharedClouds;
        var mine = clouds.Where(c => IsMine(c, user));
        var others = sharing
            ? clouds.Where(c => c.Shared && !IsMine(c, user)).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Id)
            : Enumerable.Empty<SavedCloudRecord>();
        return mine.Concat(others).Select(c => ToDto(c, user)).ToList();
    }

    /// <inheritdoc />
    public TagCloudSavedCloudDto Get(User user, Guid id)
    {
        ArgumentNullException.ThrowIfNull(user);
        TagCloudPageGate.Ensure(_configuration.GetConfiguration());
        return ToDto(Visible(user, id), user);
    }

    /// <inheritdoc />
    public async Task<TagCloudSavedCloudDto> CreateAsync(User user, TagCloudSaveRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        TagCloudPageGate.Ensure(_configuration.GetConfiguration());
        var saved = Validate(request);
        ThrowIfSharingOff(saved.Shared, wasShared: false);
        _owner.Store.ThrowIfReadOnly();
        SavedCloudRecord? created = null;
        await _owner.Store.UpdateAsync(
            document =>
            {
                var own = document.Clouds.Where(c => IsMine(c, user)).ToList();
                if (own.Count >= MaxCloudsPerUser)
                {
                    throw PluginException.Invalid(string.Format(CultureInfo.InvariantCulture, "You can keep at most {0} saved clouds.", MaxCloudsPerUser));
                }

                ThrowIfNameTaken(own, saved.Name, except: null);
                var now = _time.GetUtcNow().UtcDateTime;
                created = saved with { Id = Guid.NewGuid(), OwnerId = user.Id, CreatedUtc = now, UpdatedUtc = now };
                return SavedCloudOwner.WithClouds(document, [.. document.Clouds, created]) with { KnownUsers = document.KnownUsers.With(user) };
            },
            cancellationToken).ConfigureAwait(false);
        return ToDto(created!, user);
    }

    /// <inheritdoc />
    public async Task<TagCloudSavedCloudDto> UpdateAsync(User user, Guid id, TagCloudSaveRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        TagCloudPageGate.Ensure(_configuration.GetConfiguration());
        var saved = Validate(request);
        _owner.Store.ThrowIfReadOnly();
        SavedCloudRecord? updated = null;
        await _owner.Store.UpdateAsync(
            document =>
            {
                var current = document.Clouds.FirstOrDefault(c => c.Id == id);
                if (current is null || !CanOpen(current, user))
                {
                    throw PluginException.NotFound("That saved cloud isn't there any more.");
                }

                if (!IsMine(current, user))
                {
                    throw PluginException.Forbidden("Only its owner can change this cloud. Duplicate it to keep your own copy.");
                }

                if (request.IfUpdatedUtc is { } seen && seen.ToUniversalTime() != current.UpdatedUtc)
                {
                    throw PluginException.Conflict("This cloud was saved again since you opened it. Open it again to see the newer one.");
                }

                // With sharing off, a cloud already shared may be saved as it is or stop being shared; only turning sharing
                // on is refused.
                ThrowIfSharingOff(saved.Shared, wasShared: current.Shared);
                ThrowIfNameTaken(document.Clouds.Where(c => IsMine(c, user)), saved.Name, except: id);
                updated = saved with { Id = current.Id, OwnerId = current.OwnerId, CreatedUtc = current.CreatedUtc, UpdatedUtc = _time.GetUtcNow().UtcDateTime, Extra = current.Extra };
                return SavedCloudOwner.WithClouds(document, document.Clouds.Select(c => c.Id == id ? updated : c)) with { KnownUsers = document.KnownUsers.With(user) };
            },
            cancellationToken).ConfigureAwait(false);
        return ToDto(updated!, user);
    }

    /// <inheritdoc />
    public async Task<TagCloudSavedCloudDto> DuplicateAsync(User user, Guid id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        TagCloudPageGate.Ensure(_configuration.GetConfiguration());
        var source = Visible(user, id);
        _owner.Store.ThrowIfReadOnly();
        SavedCloudRecord? copy = null;
        await _owner.Store.UpdateAsync(
            document =>
            {
                var own = document.Clouds.Where(c => IsMine(c, user)).ToList();
                if (own.Count >= MaxCloudsPerUser)
                {
                    throw PluginException.Invalid(string.Format(CultureInfo.InvariantCulture, "You can keep at most {0} saved clouds.", MaxCloudsPerUser));
                }

                var now = _time.GetUtcNow().UtcDateTime;
                copy = source with
                {
                    Id = Guid.NewGuid(),
                    OwnerId = user.Id,
                    OwnerDeleted = false,
                    Shared = false,
                    Name = CopyName(own, source.Name),
                    CreatedUtc = now,
                    UpdatedUtc = now
                };
                return SavedCloudOwner.WithClouds(document, [.. document.Clouds, copy]) with { KnownUsers = document.KnownUsers.With(user) };
            },
            cancellationToken).ConfigureAwait(false);
        return ToDto(copy!, user);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(User user, Guid id, bool isAdmin, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        TagCloudPageGate.Ensure(_configuration.GetConfiguration());
        _owner.Store.ThrowIfReadOnly();
        await _owner.Store.UpdateAsync(
            document =>
            {
                var current = document.Clouds.FirstOrDefault(c => c.Id == id);
                if (current is null || !(CanOpen(current, user) || (isAdmin && current.Shared)))
                {
                    throw PluginException.NotFound("That saved cloud isn't there any more.");
                }

                if (!IsMine(current, user) && !(isAdmin && current.Shared))
                {
                    throw PluginException.Forbidden("Only its owner or an administrator can delete this cloud.");
                }

                return SavedCloudOwner.WithClouds(document, document.Clouds.Where(c => c.Id != id));
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Normalizes a name: control characters out, runs of white space one space, trimmed.
    /// </summary>
    /// <param name="name">The name sent.</param>
    /// <returns>The name.</returns>
    public static string NormalizeName(string? name)
    {
        var text = new StringBuilder();
        var space = false;
        foreach (var c in name ?? string.Empty)
        {
            if (char.IsWhiteSpace(c))
            {
                space = text.Length > 0;
                continue;
            }

            if (char.IsControl(c))
            {
                continue;
            }

            if (space)
            {
                text.Append(' ');
                space = false;
            }

            text.Append(c);
        }

        return text.ToString();
    }

    private static bool IsMine(SavedCloudRecord cloud, User user) => cloud.OwnerId.Equals(user.Id) && !cloud.OwnerDeleted;

    private static void ThrowIfNameTaken(IEnumerable<SavedCloudRecord> own, string name, Guid? except)
    {
        if (own.Any(c => c.Id != except && string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw PluginException.Conflict("You already have a saved cloud of that name.");
        }
    }

    private static string CopyName(IReadOnlyCollection<SavedCloudRecord> own, string name)
    {
        for (var n = 1; ; n++)
        {
            var suffix = n == 1 ? " (copy)" : string.Format(CultureInfo.InvariantCulture, " (copy {0})", n);
            var candidate = (name.Length + suffix.Length > MaxNameLength ? name[..(MaxNameLength - suffix.Length)].TrimEnd() : name) + suffix;
            if (!own.Any(c => string.Equals(c.Name, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                return candidate;
            }
        }
    }

    private void ThrowIfSharingOff(bool shared, bool wasShared)
    {
        if (shared && !wasShared && !_configuration.GetConfiguration().AllowSharedClouds)
        {
            throw PluginException.FeatureDisabled("Sharing clouds is turned off by your server admin.");
        }
    }

    private bool CanOpen(SavedCloudRecord cloud, User user)
        => IsMine(cloud, user) || (cloud.Shared && _configuration.GetConfiguration().AllowSharedClouds);

    private SavedCloudRecord Visible(User user, Guid id)
        => _owner.Store.Current.Clouds.FirstOrDefault(c => c.Id == id && CanOpen(c, user))
            ?? throw PluginException.NotFound("That saved cloud isn't there any more.");

    private SavedCloudRecord Validate(TagCloudSaveRequest? request)
    {
        if (request is null)
        {
            throw PluginException.Invalid("The cloud is missing.");
        }

        var name = NormalizeName(request.Name);
        if (name.Length == 0)
        {
            throw PluginException.Invalid("Give the cloud a name.");
        }

        if (name.Length > MaxNameLength)
        {
            throw PluginException.Invalid(string.Format(CultureInfo.InvariantCulture, "A cloud's name can be at most {0} characters.", MaxNameLength));
        }

        if (!Enum.IsDefined(request.Source) || !Enum.IsDefined(request.Scope))
        {
            throw PluginException.Invalid("That isn't a cloud this server knows.");
        }

        var libraries = (request.LibraryIds ?? []).Where(id => id != Guid.Empty).Distinct().ToList();
        if (libraries.Count > MaxLibraries)
        {
            throw PluginException.Invalid(string.Format(CultureInfo.InvariantCulture, "A cloud can count over at most {0} libraries.", MaxLibraries));
        }

        var keys = (request.NarrowKeys ?? []).Select(IndexState.KeyOf).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        if (keys.Count > TagCloudQueryService.MaxNarrowKeys)
        {
            throw PluginException.Invalid(string.Format(CultureInfo.InvariantCulture, "A cloud can be narrowed by at most {0} words.", TagCloudQueryService.MaxNarrowKeys));
        }

        return new SavedCloudRecord
        {
            Name = name,
            Shared = request.Shared,
            Source = request.Source,
            Scope = request.Scope,
            LibraryIds = libraries,
            NarrowKeys = keys,
            Look = CloudLook.Normalize(request.Look)
        };
    }

    private TagCloudSavedCloudDto ToDto(SavedCloudRecord cloud, User user) => new()
    {
        Id = cloud.Id,
        Name = cloud.Name,
        Shared = cloud.Shared,
        IsMine = IsMine(cloud, user),
        OwnerName = cloud.OwnerDeleted ? FormerUser : _users.GetUserById(cloud.OwnerId)?.Username ?? FormerUser,
        CreatedUtc = cloud.CreatedUtc,
        UpdatedUtc = cloud.UpdatedUtc,
        Source = cloud.Source,
        Scope = cloud.Scope,
        LibraryIds = cloud.LibraryIds,
        NarrowKeys = cloud.NarrowKeys,
        Look = cloud.Look.ToDto()
    };
}
