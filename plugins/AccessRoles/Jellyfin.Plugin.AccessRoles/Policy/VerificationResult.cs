using System;
using System.Collections.Generic;
using Jellyfin.Plugin.AccessRoles.Access;

namespace Jellyfin.Plugin.AccessRoles.Policy;

/// <summary>
/// The outcome of a post-write check.
/// </summary>
/// <param name="Check">The check.</param>
/// <param name="Outcome">What it found.</param>
/// <param name="Found">The library fields the server enforces now, when the user exists.</param>
internal sealed record VerificationResult(PendingVerification Check, VerificationOutcome Outcome, LibraryFields? Found);
