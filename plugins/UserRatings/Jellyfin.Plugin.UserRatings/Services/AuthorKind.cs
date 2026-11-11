namespace Jellyfin.Plugin.UserRatings.Services;

/// <summary>
/// What a row's author id is.
/// </summary>
internal enum AuthorKind
{
    /// <summary>A user of the server: their rows count unless a Hide setting leaves them out.</summary>
    Member,

    /// <summary>A former member (<c>Anonymize</c>): the rows count and show as "Former member".</summary>
    FormerMember,

    /// <summary>Neither (a deleted user's id under <c>KeepAsOrphan</c>, or found by the reconciliation): the rows are kept, count
    /// nowhere and show only to moderation.</summary>
    Orphan
}
