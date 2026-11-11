namespace Jellyfin.Plugin.SmartSearch.Query.Evaluation;

/// <summary>
/// A rule's or a group's value in the engine's three-valued evaluation (phase A): a leaf whose field
/// needs facts the candidate hasn't got yet is <see cref="Unknown"/>, and the groups follow Kleene's logic.
/// </summary>
internal enum TriState
{
    /// <summary>The item doesn't match.</summary>
    False,

    /// <summary>The item matches.</summary>
    True,

    /// <summary>The item's facts decide (phase B fetches them and evaluates again).</summary>
    Unknown
}
