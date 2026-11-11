using System.Collections.Generic;

namespace Jellyfin.Plugin.AccessRoles.Enforcement;

/// <summary>
/// The hardening's settings as one request reads them (<see cref="ServerHardening.Settings"/>).
/// </summary>
/// <param name="Library">Whether the library leak hardening applies: <c>LibraryHardening</c> isn't <c>Off</c>
/// (<c>Standard</c>, the default; <c>Strict</c> is <c>Standard</c> in this build, its own rows arrive in 1.1).</param>
/// <param name="Redact">Whether configuration reads are <c>Redact</c> (every key, secrets stripped) rather than the default
/// <c>AllowList</c>.</param>
/// <param name="AllowList">The keys non-admins may read in <c>AllowList</c> mode, normalised.</param>
/// <param name="BlockRestart">Whether a non-admin's or an anonymous caller's restart is refused.</param>
/// <param name="TrimPublicUsers">Whether <c>GET /Users/Public</c> is trimmed to the login fields.</param>
internal sealed record HardeningSettings(bool Library, bool Redact, IReadOnlySet<string> AllowList, bool BlockRestart, bool TrimPublicUsers);
