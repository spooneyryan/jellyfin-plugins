using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.AccessRoles.Access;
using Jellyfin.Plugin.AccessRoles.Storage;

namespace Jellyfin.Plugin.AccessRoles.Sync;

/// <summary>
/// What the library sync does for a managed user whose native access drifted while <c>DriftPolicy=ReportOnly</c> (the
/// "report drift only" ownership model): the admin's edit stays, the
/// changes in the safe direction are applied anyway, and the rest is reported.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Kept:</b> what the admin's edit added to the access Access Roles last wrote (the ledger's <c>LastWrite</c>), and
/// what it took away that the rules still give. An "every library" edit is kept whole: nothing can be taken from it
/// without turning it into a list.</item>
/// <item><b>Applied</b> (removals only, through <c>PolicyWriter</c>): ids of libraries that are gone, of libraries created
/// again under the same id that nothing grants again (the "Deleted" and "Re-created" rules; new libraries never reach
/// <c>Only</c> lists), and of libraries
/// Access Roles granted at its last write that the rules no longer give (native − (LastWrite − target)). The design
/// removes the first two from "every managed user" without exception, and the third undoes nothing the admin did.</item>
/// <item><b>Reported, not applied:</b> what the rules give since the last write (target − LastWrite). The design weighed three
/// ownership models: (a) re-apply, (b) "report drift only" — <c>ReportOnly</c> — and (c) "add role grants on top
/// of manual edits", which wasn't chosen; adding grants to a drifted user is (c), so they wait for the admin's Revert
/// (<c>reapplyDrift=true</c>) or Keep as override.</item>
/// <item>Removals that would leave the user no library while their access still opens one the rules no longer give are held
/// back and reported (no empty result by accident), unless the admin chose no library (<c>Only</c> with nothing
/// and <c>ConfirmNoLibraries</c>).</item>
/// </list>
/// The ledger's <c>LastWrite</c> is left as it was: the admin's edit is still drift afterwards, and is still reported.
/// </remarks>
internal sealed record DriftDecision
{
    /// <summary>Gets the library fields to write (the native access with the applied removals), or <c>null</c> when nothing is applied.</summary>
    public LibraryFields? Write { get; init; }

    /// <summary>Gets the removed ids of libraries that are gone.</summary>
    public IReadOnlyList<Guid> Deleted { get; init; } = [];

    /// <summary>Gets the removed ids of libraries created again under the same id that nothing grants again.</summary>
    public IReadOnlyList<Guid> Recreated { get; init; } = [];

    /// <summary>Gets the removed ids of libraries Access Roles granted at its last write that the rules no longer give.</summary>
    public IReadOnlyList<Guid> NoLongerGranted { get; init; } = [];

    /// <summary>Gets the ids the rules no longer give that stay, because removing them would leave the user no library.</summary>
    public IReadOnlyList<Guid> HeldBack { get; init; } = [];

    /// <summary>Gets the admin's additions to the last write, kept.</summary>
    public IReadOnlyList<Guid> KeptAdded { get; init; } = [];

    /// <summary>Gets what the rules give that the admin's edit took away, kept away.</summary>
    public IReadOnlyList<Guid> KeptRemoved { get; init; } = [];

    /// <summary>Gets a value indicating whether the admin's edit is "every library", kept whole.</summary>
    public bool KeptEveryLibrary { get; init; }

    /// <summary>Gets what the rules give since the last write: reported, not applied.</summary>
    public IReadOnlyList<Guid> NotApplied { get; init; } = [];

    /// <summary>Gets a value indicating whether the rules give every library (future ones too) since the last write, not applied.</summary>
    public bool NotAppliedEveryLibrary { get; init; }

    /// <summary>
    /// Decides what to do for a drifted user under <c>ReportOnly</c>.
    /// </summary>
    /// <param name="document">The store document, for the known libraries' created times.</param>
    /// <param name="catalog">The libraries the rules resolve against.</param>
    /// <param name="listed">The libraries the server lists now (a library withheld while a rename waits is listed, not gone).</param>
    /// <param name="native">The user's native access.</param>
    /// <param name="target">The rules' result.</param>
    /// <param name="lastWrite">Access Roles' last write for the user.</param>
    /// <param name="noLibrariesChosen">Whether the admin chose no library for the user.</param>
    /// <returns>The decision.</returns>
    public static DriftDecision Decide(AccessRolesDocument document, LibraryCatalog catalog, LibraryCatalog listed, LibraryFields native, LibraryFields target, LedgerWrite lastWrite, bool noLibrariesChosen)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(listed);
        ArgumentNullException.ThrowIfNull(native);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(lastWrite);

        bool Granted(Guid id) => target.EnableAllFolders ? catalog.Contains(id) : target.EnabledFolders.Contains(id);
        bool Ours(Guid id) => lastWrite.EnableAllFolders || lastWrite.EnabledFolders.Contains(id);
        var grantedNow = target.EnableAllFolders ? catalog.Ids.ToList() : target.EnabledFolders.Where(catalog.Contains).ToList();
        var notApplied = grantedNow.Where(id => !Ours(id) && !(native.EnableAllFolders || native.EnabledFolders.Contains(id)));
        var notAppliedAll = target.EnableAllFolders && !lastWrite.EnableAllFolders && !native.EnableAllFolders;
        if (native.EnableAllFolders)
        {
            return new DriftDecision { KeptEveryLibrary = true, NotApplied = catalog.Order(notApplied), NotAppliedEveryLibrary = notAppliedAll };
        }

        var ids = native.EnabledFolders.Distinct().ToList();
        var deleted = ids.Where(id => !listed.Contains(id) && !catalog.Contains(id)).ToList();
        var recreated = ids.Where(id => catalog.Contains(id) && EffectiveRulesResolver.IsRecreated(document, catalog, id) && !Granted(id)).ToList();
        var kept = ids.Where(id => !deleted.Contains(id) && !recreated.Contains(id)).ToList();
        var noLonger = kept.Where(id => Ours(id) && !Granted(id)).ToList();
        IReadOnlyList<Guid> heldBack = [];
        if (kept.Count > 0 && kept.All(noLonger.Contains) && !noLibrariesChosen)
        {
            heldBack = noLonger;
            noLonger = [];
        }

        var remaining = kept.Where(id => !noLonger.Contains(id)).ToList();
        var write = LibraryFields.Of(false, catalog.Order(remaining));
        var ours = lastWrite.EnableAllFolders ? catalog.Ids : lastWrite.EnabledFolders;
        return new DriftDecision
        {
            Write = write.Same(native) ? null : write,
            Deleted = deleted,
            Recreated = catalog.Order(recreated),
            NoLongerGranted = catalog.Order(noLonger),
            HeldBack = catalog.Order(heldBack),
            KeptAdded = catalog.Order(remaining.Where(id => !Ours(id))),
            KeptRemoved = catalog.Order(ours.Where(id => Granted(id) && !ids.Contains(id))),
            NotApplied = catalog.Order(notApplied),
            NotAppliedEveryLibrary = notAppliedAll
        };
    }
}
