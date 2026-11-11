using System.Collections.Generic;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.SmartSearch.Query.Evaluation;

/// <summary>
/// One candidate as a rule sees it: the item, for per-user fields the user's data for it, and once
/// phase B has fetched them, its facts.
/// </summary>
/// <param name="Item">The item.</param>
/// <param name="UserData">
/// The user's data for the item, as <c>IUserDataManager.GetUserData</c> returns it, or <c>null</c> when there is none:
/// <c>null</c> reads as unplayed, not a favourite, not in progress, play count 0 and never played.
/// </param>
/// <param name="Facts">
/// The item's facts by field id (streams, ratings), or <c>null</c> before phase B: a rule on a
/// <see cref="Fields.FieldCost.Facts"/> field is then <see cref="TriState.Unknown"/> (<see cref="Planning.RuleTree.CompileTriState"/>).
/// </param>
internal readonly record struct EvaluationRow(BaseItem Item, UserItemData? UserData = null, IReadOnlyDictionary<string, object?>? Facts = null);
