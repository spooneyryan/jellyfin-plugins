using System.Collections.Generic;

namespace Jellyfin.Plugin.LibraryMaintenance.Move.Planning;

/// <summary>
/// A plan's warning or blocker (<c>Warnings[{Code, Message}]</c>, <c>Blockers[{Code, Message, Paths?}]</c>).
/// </summary>
/// <param name="Code">The <c>move.*</c> code (<see cref="MoveErrorCodes"/>).</param>
/// <param name="Message">The sentence the dialog shows.</param>
/// <param name="Paths">The paths it is about, or null.</param>
internal sealed record PlanIssue(string Code, string Message, IReadOnlyList<string>? Paths = null);
