namespace Jellyfin.Plugin.Kit.Interop;

/// <summary>
/// An entry of a Jellyfin Enhanced data file that was left out, and why (a value of the wrong type, a missing field).
/// </summary>
/// <param name="Key">The entry's key in the file.</param>
/// <param name="Reason">Why it was left out.</param>
internal sealed record JeDataProblem(string Key, string Reason);
