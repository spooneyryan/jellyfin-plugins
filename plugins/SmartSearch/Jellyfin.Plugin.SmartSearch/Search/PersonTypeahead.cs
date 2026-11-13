using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
#if JELLYFIN_12
using Jellyfin.Data;
#endif
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.SmartSearch.Query.Evaluation;
using Jellyfin.Plugin.SmartSearch.Query.Execution;
using Jellyfin.Plugin.SmartSearch.Query.Planning;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.SmartSearch.Search;

/// <summary>
/// The <c>People</c> rule's suggestions (<c>GET /SmartSearch/People</c>): people whose name holds the text, each only when
/// an item the caller can see in their libraries credits them.
/// </summary>
/// <remarks>
/// <para><b>Candidates.</b> Core's people lookup isn't scoped to libraries on 10.11, and both lines' <c>GetPeopleItems</c>
/// load a <c>Person</c> per name, so only names are read (<c>GetPeopleNames</c>), at most <see cref="MaxNames"/>.
/// The SQL text is the longest ASCII run of the typed text (core compares <c>NameContains</c> in upper case, and SQLite
/// upper-cases ASCII only, so "Zoë" would never match): "zoë" asks for "zo", and the whole text is then matched in memory,
/// ignoring case and accents (<see cref="RuleValues.Fold"/>); a text without an ASCII letter or digit finds nobody. Jellyfin
/// 12 also asks SQL, for a caller with content restrictions, only for people credited on an item they can reach
/// (<c>InternalPeopleQuery.AccessFilter</c>, built as core's <c>PersonsController</c> builds it: the caller's access, owned
/// items and alternate versions included, here in the libraries the caller can open; none for a caller without
/// restrictions, as core sends none), and first, for an ASCII text, for the names that start with it
/// (<c>NameStartsWithOrGreater</c>/<c>NameLessThan</c>, a range on <c>lower(Name)</c>), so a name starting with the text
/// is never lost behind the cap; 10.11 has neither (nor a stable order), so there the cap can hide names past it.</para>
/// <para><b>Order.</b> Names starting with the text first, then by name.</para>
/// <para><b>The check</b> (the guarantee on both lines): in that order, at most <see cref="MaxChecks"/> names per request
/// are looked up (every one counts: a name without a <c>Person</c> item, a person already seen or hidden). A name's
/// <c>Person</c> (<c>GetPerson</c>), visible to the caller as core's own people pages ask (<c>Person.IsVisible</c>), is
/// kept only when one <c>GetItemList</c> with <c>PersonIds = [person]</c> in the physical folders of the libraries the
/// caller can open (box set libraries left out), of the field's kinds the server allows, with the caller's access query,
/// at most <see cref="ItemsChecked"/> items, returns one that isn't an extra. That query keeps only what the caller may
/// see: 12.4 in SQL (<c>ApplyAccessFiltering</c>), 10.11.5 in SQL and then <c>IsVisible(user)</c> on what it read. After
/// <see cref="MaxHiddenInARow"/> people in a row that <c>Person.IsVisible</c> hides, the request stops (a caller core
/// hides people from wholesale). A person credited only on items the caller can't see is never suggested; one whose first
/// items in scope are all dropped by 10.11's <c>IsVisible</c> alone isn't either (a rare miss, never a leak).</para>
/// <para><b>Load.</b> A request takes a search slot (<c>MaxConcurrentSearches</c>, one per user), so suggestions can't
/// crowd out searches; the request's cancellation (an aborted keystroke) stops it between checks.</para>
/// </remarks>
internal sealed class PersonTypeahead
{
    /// <summary>The most names read.</summary>
    public const int MaxNames = 1000;

    /// <summary>The most names looked up (<c>GetPerson</c>, then checked against the caller's view) per request.</summary>
    public const int MaxChecks = 100;

    /// <summary>The people in a row that <c>Person.IsVisible</c> hides after which a request stops.</summary>
    public const int MaxHiddenInARow = 10;

    /// <summary>The most of a person's items read for the check.</summary>
    public const int ItemsChecked = 10;

    private readonly ILibraryManager _library;
    private readonly LibraryAccess _access;
    private readonly EngineSlots? _slots;
    private readonly Func<Person, User, bool> _personVisible;

    /// <summary>
    /// Initializes a new instance of the <see cref="PersonTypeahead"/> class.
    /// </summary>
    /// <param name="library">The library manager.</param>
    /// <param name="access">The kit's library access.</param>
    /// <param name="slots">The engine's slots, or <c>null</c> to take none.</param>
    /// <param name="personVisible">Whether a person is visible to a user; <c>null</c> for <c>Person.IsVisible</c>.</param>
    public PersonTypeahead(ILibraryManager library, LibraryAccess access, EngineSlots? slots = null, Func<Person, User, bool>? personVisible = null)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(access);
        _library = library;
        _access = access;
        _slots = slots;
        _personVisible = personVisible ?? (static (person, user) => person.IsVisible(user));
    }

    /// <summary>Finds the people credited on something the user can see whose name holds the text.</summary>
    /// <param name="user">The signed-in user.</param>
    /// <param name="text">The text, trimmed, at least 2 characters.</param>
    /// <param name="limit">The most people.</param>
    /// <param name="kinds">The item kinds that count (the field's that the server allows).</param>
    /// <param name="cancellationToken">The request's cancellation token, checked before each person.</param>
    /// <returns>The people, those whose name starts with the text first, then by name.</returns>
    /// <exception cref="SmartSearchRefusedException">No search slot came free in time (<c>Busy</c>).</exception>
    public async Task<IReadOnlyList<Person>> FindAsync(User user, string text, int limit, IReadOnlyCollection<BaseItemKind> kinds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(kinds);
        var folders = LibraryAccess.TopParentIds(_access.VisibleLibraries(user).Where(l => l.CollectionType != CollectionType.boxsets));
        var folded = RuleValues.Fold(text);
        var sqlText = SqlText(text);
        if (folders.Length == 0 || kinds.Count == 0 || limit < 1 || sqlText is null || folded.Length == 0)
        {
            return [];
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var slot = _slots is null ? null : await _slots.EnterAsync(EvaluationPurpose.Search, user.Id, cancellationToken).ConfigureAwait(false);
        var names = CandidateNames(user, folders, kinds, text, sqlText, cancellationToken)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.Ordinal)
            .Select(n => (Name: n, Folded: RuleValues.Fold(n)))
            .Where(n => n.Folded.Contains(folded, StringComparison.Ordinal))
            .OrderByDescending(n => n.Folded.StartsWith(folded, StringComparison.Ordinal))
            .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(n => n.Name, StringComparer.Ordinal);
        var found = new List<Person>();
        var seen = new HashSet<Guid>();
        var checks = 0;
        var hiddenInARow = 0;
        foreach (var (name, _) in names)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (found.Count == limit || checks == MaxChecks || hiddenInARow == MaxHiddenInARow)
            {
                break;
            }

            checks++;
            if (_library.GetPerson(name) is not { } person || !seen.Add(person.Id))
            {
                continue;
            }

            if (!_personVisible(person, user))
            {
                hiddenInARow++;
                continue;
            }

            hiddenInARow = 0;
            if (CreditedInView(user, folders, kinds, person))
            {
                found.Add(person);
            }
        }

        return found;
    }

    /// <summary>Gets the text core is asked for: the longest run of ASCII characters holding a letter or digit, trimmed.</summary>
    /// <param name="text">The typed text.</param>
    /// <returns>The run, or <c>null</c> when the text has no ASCII letter or digit.</returns>
    internal static string? SqlText(string text)
    {
        string? best = null;
        var start = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            if (i < text.Length && text[i] is >= ' ' and <= '~')
            {
                continue;
            }

            var run = text[start..i].Trim();
            if (run.Any(char.IsAsciiLetterOrDigit) && (best is null || run.Length > best.Length))
            {
                best = run;
            }

            start = i + 1;
        }

        return best;
    }

    private static InternalItemsQuery AccessQuery(User user, Guid[] folders, IReadOnlyCollection<BaseItemKind> kinds)
    {
        var query = QueryPlanner.CreateAccessQuery(user, PlanPurpose.Search);
        query.TopParentIds = folders;
        query.IncludeItemTypes = [.. kinds];
        query.IsVirtualItem = false;
        query.EnableTotalRecordCount = false;
        query.DtoOptions = new DtoOptions(false) { EnableImages = false, EnableUserData = false };
        return query;
    }

    // The names to check, at most MaxNames. Jellyfin 12: in SQL, for a caller with content restrictions only those credited
    // on an item they can reach (as core's PersonsController asks), those starting with an ASCII text first; 10.11 can't
    // scope or order them.
    private IEnumerable<string> CandidateNames(User user, Guid[] folders, IReadOnlyCollection<BaseItemKind> kinds, string text, string sqlText, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
#if JELLYFIN_12
        var names = new List<string>();
        if (text.All(c => c is >= ' ' and <= '~'))
        {
            var lower = text.ToLowerInvariant();
            names.AddRange(_library.GetPeopleNames(new InternalPeopleQuery
            {
                NameStartsWithOrGreater = lower,
                NameLessThan = lower[..^1] + (char)(lower[^1] + 1),
                AccessFilter = NamesAccessFilter(user, folders),
                Limit = MaxNames
            }));
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (names.Count < MaxNames)
        {
            names.AddRange(_library.GetPeopleNames(new InternalPeopleQuery
            {
                NameContains = sqlText,
                AccessFilter = NamesAccessFilter(user, folders),
                Limit = MaxNames - names.Count
            }));
        }

        return names;
#else
        return _library.GetPeopleNames(new InternalPeopleQuery { NameContains = sqlText, Limit = MaxNames });
#endif
    }

#if JELLYFIN_12
    // Core's PersonsController.BuildAccessFilter: none without content restrictions, else the user's access with owned
    // items (alternate versions) included; here in the libraries the caller can open.
    private static InternalItemsQuery? NamesAccessFilter(User user, Guid[] folders)
        => user.HasContentRestrictions() ? new InternalItemsQuery(user) { TopParentIds = folders, IncludeOwnedItems = true } : null;
#endif

    // One GetItemList with the caller's access (12.4: in SQL; 10.11.5: SQL, then IsVisible(user)); an extra doesn't count.
    private bool CreditedInView(User user, Guid[] folders, IReadOnlyCollection<BaseItemKind> kinds, Person person)
    {
        var query = AccessQuery(user, folders, kinds);
        query.PersonIds = [person.Id];
        query.Limit = ItemsChecked;
        return _library.GetItemList(query).Any(i => !LibraryAccess.IsOwned(i));
    }
}
