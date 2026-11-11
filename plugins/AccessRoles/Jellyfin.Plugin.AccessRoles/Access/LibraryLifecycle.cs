using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.AccessRoles.Model;
using Jellyfin.Plugin.AccessRoles.Storage;

namespace Jellyfin.Plugin.AccessRoles.Access;

/// <summary>
/// The library lifecycle (the library structure filter's calls and the backstop's
/// diff): the libraries the server lists now, diffed against <c>KnownLibraries</c>, with the renames and removals the
/// library structure filter saw, give the known libraries, the re-pointed and orphaned references, and what happened.
/// A pure function: the library sync runs it and saves its document.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Existence</b> comes from the catalog only (<c>GetVirtualFolders()</c>); ids are the core's
/// <c>GetNewItemId</c> of the library's folder, never <c>GetVirtualFolders().ItemId</c>.</item>
/// <item><b>Ambiguous states</b>: a library listed without its item (the add window, an unrefreshed rename) is
/// not recorded yet, and a known library that vanished while such a library has the same folders, or while a rename the
/// filter saw names it, waits: it stays in <see cref="LifecycleResult.Waiting"/>, whose grants the sync keeps. The library
/// it waits for (the pending rename's new name, or the candidates with the same folders) is
/// <see cref="LifecycleResult.Withheld"/>: kept out of the catalog the rules resolve against until the rename is re-pointed,
/// so no <c>All…except</c> role or user <c>Remove</c> that names the old id grants it under its new one (without it,
/// the renamed excluded library was visible to restricted users for up to 2 s, and with <c>refreshLibrary=false</c> after a
/// scan until the next run). It has no item while it waits, so nobody could open it anyway; <c>Only</c> users keep the old
/// id.</item>
/// <item><b>Renames:</b> exact for a rename the filter saw, once a library with the new name <i>and an item</i> is listed
/// (with <c>refreshLibrary=false</c> that waits for the next scan); in the backstop, a vanished library whose
/// non-empty folders equal one new library's is a rename too (a case-only rename on Windows is one: the new name's id is
/// new). Roles, overrides, the ledger's original access and pending releases are re-pointed to the new id.</item>
/// <item><b>Deleted:</b> a vanished library (the filter saw it go, or nothing explains it) is <c>Orphaned</c>, and so are
/// the roles' references to it; nothing is removed until the admin purges it.</item>
/// <item><b>Re-created</b> (same id): a known library whose folder is newer than its recorded created time, or an
/// <c>Orphaned</c> one listed again, is marked <see cref="KnownLibrary.RecreatedUtc"/>; its old created time stays, so no
/// earlier grant gives it until the admin grants it again (<see cref="GrantAgain"/>).</item>
/// </list>
/// </remarks>
internal static class LibraryLifecycle
{
    /// <summary>How long a rename the filter saw waits for its new library before the old one counts as deleted.</summary>
    public static readonly TimeSpan PendingRenameLimit = TimeSpan.FromDays(7);

    private static readonly TimeSpan _createdTolerance = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Reconciles the known libraries with the catalog.
    /// </summary>
    /// <param name="document">The store document.</param>
    /// <param name="catalog">The libraries the server lists now.</param>
    /// <param name="removedSeen">The libraries the filter saw removed since the last reconcile (no rename can explain them).</param>
    /// <param name="now">The time, UTC.</param>
    /// <returns>The document with the lifecycle applied (the same instance when nothing changed), and what happened.</returns>
    public static LifecycleResult Reconcile(AccessRolesDocument document, LibraryCatalog catalog, IReadOnlySet<Guid> removedSeen, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(removedSeen);

        var events = new List<LifecycleEvent>();
        var known = new List<KnownLibrary>();
        foreach (var library in document.KnownLibraries)
        {
            if (!known.Exists(k => k.Id.Equals(library.Id)))
            {
                known.Add(library);
            }
        }

        var firstPopulation = known.Count == 0;
        var live = catalog.Libraries;
        var liveIds = live.Select(l => l.Id).ToHashSet();
        var renames = document.PendingRenames.ToList();
        var repoint = new Dictionary<Guid, CatalogLibrary>();
        var changed = false;

        // 1. Renames the filter saw: complete once the new name is listed with its item; drop one the server undid, or one
        // that waited too long (its old library then counts as deleted below). Until then the new name's library is withheld.
        var withheld = new HashSet<Guid>();
        foreach (var rename in document.PendingRenames)
        {
            var target = live.FirstOrDefault(l => string.Equals(l.Name, rename.NewName, StringComparison.Ordinal));
            if (target is { HasItem: true })
            {
                renames.Remove(rename);
                changed = true;
                if (!target.Id.Equals(rename.OldId))
                {
                    var old = known.Find(k => k.Id.Equals(rename.OldId));
                    repoint[rename.OldId] = target;
                    events.Add(new LifecycleEvent(LifecycleEventKind.Renamed, target.Id, target.Name, rename.OldId, old?.Name ?? rename.OldName, Exact: true));
                }
            }
            else if (liveIds.Contains(rename.OldId) || now - rename.RequestedUtc > PendingRenameLimit)
            {
                renames.Remove(rename);
                changed = true;
            }
            else if (target is not null && !target.Id.Equals(rename.OldId))
            {
                withheld.Add(target.Id);
            }
        }

        // 2. Known libraries the server no longer lists: renamed (backstop), waiting, or deleted.
        var waiting = new HashSet<Guid>();
        var orphaned = new HashSet<Guid>();
        foreach (var library in known.Where(k => k.State.Is(AccessRolesLibraryState.Active) && !liveIds.Contains(k.Id) && !repoint.ContainsKey(k.Id)))
        {
            if (renames.Exists(r => r.OldId.Equals(library.Id)))
            {
                waiting.Add(library.Id);
                continue;
            }

            if (removedSeen.Contains(library.Id))
            {
                orphaned.Add(library.Id);
                events.Add(new LifecycleEvent(LifecycleEventKind.Removed, library.Id, library.Name, null, null, Exact: true));
                continue;
            }

            var candidates = library.Locations.Count == 0
                ? []
                : live.Where(l => !known.Exists(k => k.Id.Equals(l.Id)) && !repoint.Values.Any(r => r.Id.Equals(l.Id)) && SameLocations(l.Locations, library.Locations)).ToList();
            if (candidates.Exists(c => !c.HasItem))
            {
                // Fail closed: every candidate stays out of the rules' catalog until the rename resolves.
                waiting.Add(library.Id);
                withheld.UnionWith(candidates.Select(c => c.Id));
            }
            else if (candidates.Count == 1)
            {
                repoint[library.Id] = candidates[0];
                events.Add(new LifecycleEvent(LifecycleEventKind.Renamed, candidates[0].Id, candidates[0].Name, library.Id, library.Name, Exact: false));
            }
            else
            {
                orphaned.Add(library.Id);
                events.Add(new LifecycleEvent(LifecycleEventKind.Removed, library.Id, library.Name, null, null, Exact: false)
                {
                    RenameCandidates = [.. candidates.Select(c => c.Id)]
                });
            }
        }

        // 3. The libraries the server lists: recorded once they have their item; re-created ones marked.
        var renamedTo = repoint.Values.Select(t => t.Id).ToHashSet();
        var result = new List<KnownLibrary>();
        foreach (var library in known)
        {
            if (repoint.ContainsKey(library.Id))
            {
                changed = true;
                continue;
            }

            if (orphaned.Contains(library.Id))
            {
                result.Add(library with { State = AccessRolesLibraryState.Orphaned });
                changed = true;
                continue;
            }

            if (live.FirstOrDefault(l => l.Id.Equals(library.Id)) is not { } listed)
            {
                result.Add(library);
                continue;
            }

            var updated = library with
            {
                Name = listed.Name,
                Locations = listed.Locations,
                Enabled = listed.Enabled,
                CreatedUtc = library.CreatedUtc ?? listed.CreatedUtc
            };

            if (library.State.Is(AccessRolesLibraryState.Orphaned) && listed.HasItem)
            {
                // Deleted, and created again under the same id: a new library.
                updated = updated with { State = AccessRolesLibraryState.Active, RecreatedUtc = library.RecreatedUtc ?? now };
                events.Add(new LifecycleEvent(LifecycleEventKind.Recreated, library.Id, listed.Name, null, null, Exact: false));
            }
            else if (library.State.Is(AccessRolesLibraryState.Active) && library.RecreatedUtc is null
                && library.CreatedUtc is { } recorded && listed.CreatedUtc is { } folder && folder > recorded + _createdTolerance)
            {
                // Deleted and created again between two reconciles (or while the plugin was off): a newer folder.
                updated = updated with { RecreatedUtc = now };
                events.Add(new LifecycleEvent(LifecycleEventKind.Recreated, library.Id, listed.Name, null, null, Exact: false));
            }

            if (!SameKnown(library, updated))
            {
                changed = true;
            }

            result.Add(updated);
        }

        foreach (var library in live.Where(l => l.HasItem && !known.Exists(k => k.Id.Equals(l.Id))))
        {
            var previous = repoint.FirstOrDefault(p => p.Value.Id.Equals(library.Id));
            var before = previous.Value is null ? null : known.Find(k => k.Id.Equals(previous.Key));
            result.Add(new KnownLibrary
            {
                Id = library.Id,
                Name = library.Name,
                Locations = library.Locations,
                Enabled = library.Enabled,
                CreatedUtc = library.CreatedUtc,
                FirstSeenUtc = before?.FirstSeenUtc ?? now,
                State = AccessRolesLibraryState.Active
            });
            changed = true;
            if (!firstPopulation && !renamedTo.Contains(library.Id))
            {
                events.Add(new LifecycleEvent(LifecycleEventKind.Added, library.Id, library.Name, null, null, Exact: false));
            }
        }

        // 4. References: re-pointed for renames, Orphaned for libraries deleted or never seen.
        var knownIds = result.Select(k => k.Id).ToHashSet();
        var roles = document.Roles.Select(role =>
        {
            var refs = role.Libraries.Refs.Select(r =>
            {
                if (repoint.TryGetValue(r.Id, out var target))
                {
                    return r with { Id = target.Id, NameAtSelection = target.Name, Locations = target.Locations, Orphaned = false };
                }

                var gone = orphaned.Contains(r.Id) || (!liveIds.Contains(r.Id) && !waiting.Contains(r.Id) && !knownIds.Contains(r.Id));
                return gone && !r.Orphaned ? r with { Orphaned = true } : r;
            }).ToList();
            var distinct = refs.GroupBy(r => r.Id).Select(g => g.First()).ToList();
            return distinct.SequenceEqual(role.Libraries.Refs) ? role : role with { Libraries = role.Libraries with { Refs = distinct } };
        }).ToList();
        changed |= !roles.SequenceEqual(document.Roles);

        var assignments = document.Assignments.Select(a =>
        {
            var add = Repoint(a.Libraries.Add, repoint);
            var remove = Repoint(a.Libraries.Remove, repoint);
            return add.SequenceEqual(a.Libraries.Add) && remove.SequenceEqual(a.Libraries.Remove)
                ? a
                : a with { Libraries = a.Libraries with { Add = add, Remove = remove } };
        }).ToList();
        changed |= !assignments.SequenceEqual(document.Assignments);

        var ledger = new Dictionary<Guid, LedgerEntry>();
        var ledgerChanged = false;
        foreach (var (userId, entry) in document.Ledger)
        {
            var original = Repoint(entry.OriginalEnabledFolders, repoint);
            var release = entry.Release;
            if (release is not null)
            {
                var folders = Repoint(release.EnabledFolders, repoint);
                if (!folders.SequenceEqual(release.EnabledFolders))
                {
                    release = release with { EnabledFolders = folders };
                }
            }

            var next = original.SequenceEqual(entry.OriginalEnabledFolders) && ReferenceEquals(release, entry.Release)
                ? entry
                : entry with { OriginalEnabledFolders = original, Release = release };
            ledgerChanged |= !ReferenceEquals(next, entry);
            ledger[userId] = next;
        }

        changed |= ledgerChanged;
        withheld.ExceptWith(renamedTo);
        if (!changed)
        {
            return new LifecycleResult(document, events, waiting, Catalog(catalog, known, waiting, withheld)) { Withheld = withheld };
        }

        var after = document with
        {
            KnownLibraries = result,
            PendingRenames = renames,
            Roles = roles,
            Assignments = assignments,
            Ledger = ledgerChanged ? ledger : document.Ledger
        };
        return new LifecycleResult(after, events, waiting, Catalog(catalog, result, waiting, withheld)) { Withheld = withheld };
    }

    /// <summary>
    /// Records a rename the library structure filter saw: the old library's id and the new name, kept
    /// until a library with the new name and an item is listed. A rename of a library whose own rename is still pending
    /// (A → B, then B → C) extends that one.
    /// </summary>
    /// <param name="document">The store document.</param>
    /// <param name="oldId">The id of the library before the rename.</param>
    /// <param name="oldName">Its name before the rename.</param>
    /// <param name="newName">The new name.</param>
    /// <param name="now">The time, UTC.</param>
    /// <returns>The document with the rename recorded.</returns>
    public static AccessRolesDocument RecordRename(AccessRolesDocument document, Guid oldId, string oldName, string newName, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrEmpty(oldName);
        ArgumentException.ThrowIfNullOrEmpty(newName);
        if (string.Equals(oldName, newName, StringComparison.Ordinal) || oldId.Equals(Guid.Empty))
        {
            return document;
        }

        var renames = document.PendingRenames.ToList();
        var chained = renames.FindIndex(r => string.Equals(r.NewName, oldName, StringComparison.Ordinal));
        if (chained >= 0)
        {
            renames[chained] = renames[chained] with { NewName = newName, RequestedUtc = now };
        }
        else
        {
            renames.RemoveAll(r => r.OldId.Equals(oldId));
            renames.Add(new PendingRename { OldId = oldId, OldName = oldName, NewName = newName, RequestedUtc = now });
        }

        return document with { PendingRenames = renames };
    }

    /// <summary>
    /// The admin's "grant it again" for a re-created library (the Libraries tab's Grant again): its
    /// recorded created time becomes its folder's, its re-created mark goes, and the roles' references to it are no longer
    /// <c>Orphaned</c>, so every earlier grant (a role's <c>Only</c> list, a user's <c>Add</c>, the original access) gives
    /// it again.
    /// </summary>
    /// <param name="document">The store document.</param>
    /// <param name="catalog">The libraries now.</param>
    /// <param name="libraryId">The library.</param>
    /// <param name="now">The time, UTC.</param>
    /// <returns>The document, or <c>null</c> when the library isn't re-created and no reference to it is orphaned.</returns>
    public static AccessRolesDocument? GrantAgain(AccessRolesDocument document, LibraryCatalog catalog, Guid libraryId, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(catalog);
        if (catalog.Find(libraryId) is not { } library)
        {
            return null;
        }

        var recreated = EffectiveRulesResolver.IsRecreated(document, catalog, libraryId);
        var orphanedRefs = document.Roles.Any(r => r.Libraries.Refs.Any(x => x.Id.Equals(libraryId) && x.Orphaned));
        if (!recreated && !orphanedRefs)
        {
            return null;
        }

        var knownEntry = document.KnownLibraries.FirstOrDefault(k => k.Id.Equals(libraryId));
        var refreshed = knownEntry is null
            ? new KnownLibrary { Id = library.Id, Name = library.Name, Locations = library.Locations, Enabled = library.Enabled, CreatedUtc = library.CreatedUtc, FirstSeenUtc = now }
            : knownEntry with { CreatedUtc = library.CreatedUtc ?? knownEntry.CreatedUtc, RecreatedUtc = null, State = AccessRolesLibraryState.Active, Name = library.Name, Locations = library.Locations, Enabled = library.Enabled };
        return document with
        {
            KnownLibraries = knownEntry is null ? [.. document.KnownLibraries, refreshed] : [.. document.KnownLibraries.Select(k => k.Id.Equals(libraryId) ? refreshed : k)],
            Roles = [.. document.Roles.Select(r => r.Libraries.Refs.Any(x => x.Id.Equals(libraryId) && x.Orphaned)
                ? r with { Libraries = r.Libraries with { Refs = [.. r.Libraries.Refs.Select(x => x.Id.Equals(libraryId) ? x with { Orphaned = false, NameAtSelection = library.Name, Locations = library.Locations } : x)] } }
                : r)]
        };
    }

    /// <summary>
    /// Purges a deleted library's references (<c>Libraries/{id}/Purge</c>). Its grants go: the
    /// <i>Only…</i> roles' lists, the users' <c>Add</c>, the ledger's original access and pending releases, the pending
    /// renames. Its exclusions stay (1.0.1, the final verification's purge finding): an <i>All except…</i> role (or a mode a
    /// newer build wrote, fail closed) and a user's <c>Remove</c> list keep leaving it out, and while one does,
    /// <c>KnownLibraries</c> keeps it as a known library the server doesn't list (<c>Orphaned</c>), so a library created
    /// again under its id is re-created (<see cref="EffectiveRulesResolver.IsRecreated"/>): still left out of them, also
    /// after the admin grants it again (<see cref="GrantAgain"/> touches no exclusion), and given only by what gives any new
    /// library (an <i>All</i> role, an <i>All except…</i> role that doesn't name it). With no exclusion left the entry goes
    /// too.
    /// </summary>
    /// <param name="document">The store document.</param>
    /// <param name="libraryId">The library, which the server no longer lists.</param>
    /// <returns>The document, or <c>null</c> when the purge changes nothing (nothing refers to the library, or only its
    /// exclusions do, <see cref="IsExcluded"/>).</returns>
    public static AccessRolesDocument? Purge(AccessRolesDocument document, Guid libraryId)
    {
        ArgumentNullException.ThrowIfNull(document);
        var changed = false;
        var roles = document.Roles.Select(r =>
        {
            if (KeepsExclusion(r) || !r.Libraries.Refs.Any(x => x.Id.Equals(libraryId)))
            {
                return r;
            }

            changed = true;
            return r with { Libraries = r.Libraries with { Refs = [.. r.Libraries.Refs.Where(x => !x.Id.Equals(libraryId))] } };
        }).ToList();
        var assignments = document.Assignments.Select(a =>
        {
            if (!a.Libraries.Add.Contains(libraryId))
            {
                return a;
            }

            changed = true;
            return a with { Libraries = a.Libraries with { Add = [.. a.Libraries.Add.Where(id => !id.Equals(libraryId))] } };
        }).ToList();
        var ledgerChanged = document.Ledger.Values.Any(e => e.OriginalEnabledFolders.Contains(libraryId) || (e.Release?.EnabledFolders.Contains(libraryId) ?? false));
        var renamesChanged = document.PendingRenames.Any(r => r.OldId.Equals(libraryId));

        // The known library: kept, as one the server doesn't list, while an exclusion names it; else it goes.
        var excluded = IsExcluded(document, libraryId);
        var known = document.KnownLibraries;
        if (!excluded && known.Any(k => k.Id.Equals(libraryId)))
        {
            known = [.. known.Where(k => !k.Id.Equals(libraryId))];
        }
        else if (excluded && known.Any(k => k.Id.Equals(libraryId) && !k.State.Is(AccessRolesLibraryState.Orphaned)))
        {
            known = [.. known.Select(k => k.Id.Equals(libraryId) ? k with { State = AccessRolesLibraryState.Orphaned } : k)];
        }

        if (!changed && !ledgerChanged && !renamesChanged && ReferenceEquals(known, document.KnownLibraries))
        {
            return null;
        }

        return document with
        {
            Roles = roles,
            Assignments = assignments,
            Ledger = !ledgerChanged ? document.Ledger : document.Ledger.ToDictionary(
                p => p.Key,
                p => p.Value.OriginalEnabledFolders.Contains(libraryId) || (p.Value.Release?.EnabledFolders.Contains(libraryId) ?? false)
                    ? p.Value with
                    {
                        OriginalEnabledFolders = [.. p.Value.OriginalEnabledFolders.Where(id => !id.Equals(libraryId))],
                        Release = p.Value.Release is { } release ? release with { EnabledFolders = [.. release.EnabledFolders.Where(id => !id.Equals(libraryId))] } : null
                    }
                    : p.Value),
            KnownLibraries = known,
            PendingRenames = renamesChanged ? [.. document.PendingRenames.Where(r => !r.OldId.Equals(libraryId))] : document.PendingRenames
        };
    }

    /// <summary>
    /// Gets whether a library is left out by an exclusion, which a purge keeps (1.0.1): an <i>All except…</i> role (or a
    /// mode a newer build wrote) naming it, or a user's <c>Remove</c> list.
    /// </summary>
    /// <param name="document">The store document.</param>
    /// <param name="libraryId">The library.</param>
    /// <returns><c>true</c> when an exclusion names it.</returns>
    public static bool IsExcluded(AccessRolesDocument document, Guid libraryId)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.Roles.Any(r => KeepsExclusion(r) && r.Libraries.Refs.Any(x => x.Id.Equals(libraryId)))
            || document.Assignments.Any(a => a.Libraries.Remove.Contains(libraryId));
    }

    // Whether a role's references leave libraries out, so a purge keeps them: every mode but the ones that grant what they
    // list (Only…) or list nothing (All, NotManaged); a mode a newer build wrote is kept (fail closed).
    private static bool KeepsExclusion(AccessRole role)
        => !(role.Libraries.Mode.Is(AccessRolesRoleLibraryMode.Only) || role.Libraries.Mode.Is(AccessRolesRoleLibraryMode.All) || role.Libraries.Mode.Is(AccessRolesRoleLibraryMode.NotManaged));

    private static List<Guid> Repoint(IReadOnlyList<Guid> ids, IReadOnlyDictionary<Guid, CatalogLibrary> repoint)
        => repoint.Count == 0 ? [.. ids] : [.. ids.Select(id => repoint.TryGetValue(id, out var target) ? target.Id : id).Distinct()];

    private static bool SameLocations(IReadOnlyList<string> a, IReadOnlyList<string> b)
        => a.Count > 0 && a.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(b);

    private static bool SameKnown(KnownLibrary a, KnownLibrary b)
        => string.Equals(a.Name, b.Name, StringComparison.Ordinal)
            && a.Locations.SequenceEqual(b.Locations, StringComparer.Ordinal)
            && a.Enabled == b.Enabled
            && a.CreatedUtc == b.CreatedUtc
            && a.RecreatedUtc == b.RecreatedUtc
            && a.State == b.State;

    // The catalog the sync resolves against: the libraries listed now, plus the known ones that wait for a rename to
    // resolve, so their grants stay until the rename is re-pointed, and without the libraries
    // they wait for, which no rule may grant before the re-pointing.
    private static LibraryCatalog Catalog(LibraryCatalog catalog, IReadOnlyList<KnownLibrary> known, IReadOnlySet<Guid> waiting, IReadOnlySet<Guid> withheld)
        => waiting.Count == 0 && withheld.Count == 0
            ? catalog
            : new LibraryCatalog([
                .. catalog.Libraries.Where(l => !withheld.Contains(l.Id)),
                .. known.Where(k => waiting.Contains(k.Id)).Select(k => new CatalogLibrary(k.Id, k.Name, k.Locations, k.Enabled, null, k.CreatedUtc, HasItem: false))]);
}
