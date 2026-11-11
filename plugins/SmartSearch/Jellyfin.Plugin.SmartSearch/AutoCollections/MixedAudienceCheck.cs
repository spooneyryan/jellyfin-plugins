using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.Users;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.SmartSearch.AutoCollections;

/// <summary>
/// Tells whether some people who can see a collection can't open all of its members: a member in a library they can't
/// open, above their parental limit, or hidden from them by a tag. They see only the members they may open; the server
/// shows nothing of the others (a collection has no generated image), so this is a warning, not a refusal.
/// </summary>
/// <remarks>
/// <para>Who can see the collection: the users who can open a collections library, or every user while the server has none
/// yet (the first collection creates it, visible to the users who see every library).</para>
/// <para>How each is asked depends on their rules. A user without a parental limit, blocked unrated kinds or tag rules sees
/// every item in the libraries they can open, so for them it comes down to the libraries: none is asked when they can
/// open every library the members come from (the creator's), else one id-only query per 500 members. A user with such
/// rules is asked through the same check the engine's results pass (<see cref="LibraryAccess.EligibleIds"/>, which loads
/// the items), and within a pass each answer is kept for the definitions after it (<see cref="AudienceMemo"/>).</para>
/// </remarks>
internal sealed class MixedAudienceCheck
{
    private readonly LibraryAccess _access;
    private readonly IUserManager _users;
    private readonly ILibraryManager? _library;

    /// <summary>
    /// Initializes a new instance of the <see cref="MixedAudienceCheck"/> class.
    /// </summary>
    /// <param name="access">The kit's library access.</param>
    /// <param name="users">The user manager.</param>
    /// <param name="library">The library manager (id-only queries); without it every user is asked item by item.</param>
    public MixedAudienceCheck(LibraryAccess access, IUserManager users, ILibraryManager? library = null)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(users);
        _access = access;
        _users = users;
        _library = library;
    }

    /// <summary>Counts the users who can see the collection but not all of its members.</summary>
    /// <param name="members">The members.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <param name="sourceLibraries">The libraries every member comes from (the creator's), or <c>null</c> when unknown.</param>
    /// <param name="memo">The answers kept for the pass, or <c>null</c>.</param>
    /// <returns>How many users see only part of the collection; 0 when everyone who sees it sees all of it.</returns>
    public int CountPartialViewers(IReadOnlyCollection<Guid> members, CancellationToken cancellationToken, IReadOnlyCollection<Guid>? sourceLibraries = null, AudienceMemo? memo = null)
    {
        ArgumentNullException.ThrowIfNull(members);
        var wanted = members.Where(id => !id.Equals(Guid.Empty)).Distinct().ToList();
        if (wanted.Count == 0)
        {
            return 0;
        }

        var anyCollectionsLibrary = _access.VisibleLibraries(null).Any(l => l.CollectionType == CollectionType.boxsets);
        var partial = 0;
        foreach (var user in KitUsers.GetAll(_users))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (user.HasPermission(PermissionKind.IsDisabled))
            {
                continue;
            }

            var libraries = _access.VisibleLibraries(user);
            var seesCollections = anyCollectionsLibrary
                ? libraries.Any(l => l.CollectionType == CollectionType.boxsets)
                : user.HasPermission(PermissionKind.EnableAllFolders);
            if (!seesCollections)
            {
                continue;
            }

            if (SeesFewer(user, libraries, wanted, sourceLibraries, memo))
            {
                partial++;
            }
        }

        return partial;
    }

    // Whether a user without parental or tag rules is limited by their libraries only.
    private static bool HasNoItemRules(User user)
        => user.MaxParentalRatingScore is null
            && user.MaxParentalRatingSubScore is null
            && user.GetPreference(PreferenceKind.BlockedTags).All(string.IsNullOrWhiteSpace)
            && user.GetPreference(PreferenceKind.AllowedTags).All(string.IsNullOrWhiteSpace)
            && user.GetPreferenceValues<UnratedItem>(PreferenceKind.BlockUnratedItems).Length == 0;

    private bool SeesFewer(User user, IReadOnlyList<CollectionFolder> libraries, List<Guid> wanted, IReadOnlyCollection<Guid>? sourceLibraries, AudienceMemo? memo)
    {
        if (_library is not null && HasNoItemRules(user))
        {
            if (sourceLibraries is not null && sourceLibraries.All(id => libraries.Any(l => l.Id.Equals(id))))
            {
                return false;
            }

            var topParents = LibraryAccess.TopParentIds(libraries);
            if (topParents.Length == 0)
            {
                return true;
            }

            var found = 0;
            foreach (var chunk in wanted.Chunk(LibraryAccess.IdsPerQuery))
            {
                var query = new InternalItemsQuery { ItemIds = chunk, TopParentIds = topParents, GroupByPresentationUniqueKey = false };
                found += (_library.GetItemIds(query) ?? []).Distinct().Count();
            }

            return found < wanted.Count;
        }

        if (memo is null)
        {
            return _access.EligibleIds(user, libraries, wanted).Count < wanted.Count;
        }

        var known = memo.For(user.Id);
        var unknown = wanted.Where(id => !known.ContainsKey(id)).ToList();
        if (unknown.Count > 0)
        {
            var eligible = _access.EligibleIds(user, libraries, unknown);
            foreach (var id in unknown)
            {
                known[id] = eligible.Contains(id);
            }
        }

        return wanted.Any(id => !known[id]);
    }
}
