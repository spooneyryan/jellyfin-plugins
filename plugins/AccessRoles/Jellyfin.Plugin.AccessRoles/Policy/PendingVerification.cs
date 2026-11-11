using System;
using System.Collections.Generic;
using Jellyfin.Plugin.AccessRoles.Access;

namespace Jellyfin.Plugin.AccessRoles.Policy;

/// <summary>
/// A check, about <see cref="PolicyWriter.VerifyDelay"/> after a write, that the server still enforces what was written
/// (measured on 10.11.5: a sign-in racing the write can put the old user back
/// into the user manager's cache).
/// </summary>
/// <param name="UserId">The user.</param>
/// <param name="Target">The library fields written.</param>
/// <param name="Before">The library fields the write replaced: a cache revert brings these back.</param>
/// <param name="DueUtc">When to check, UTC.</param>
/// <param name="Attempt">0 after a write, 1 after the write again that a revert caused (no third write: the periodic
/// reconcile heals what is left).</param>
internal sealed record PendingVerification(Guid UserId, LibraryFields Target, LibraryFields Before, DateTime DueUtc, int Attempt);
