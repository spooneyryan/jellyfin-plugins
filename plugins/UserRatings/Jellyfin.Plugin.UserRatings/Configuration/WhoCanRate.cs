namespace Jellyfin.Plugin.UserRatings.Configuration;

/// <summary>
/// Who may create and edit ratings and reviews (<see cref="PluginConfiguration.WhoCanRate"/>). Everyone
/// reads every rating and review on items they can open, whatever the value.
/// </summary>
internal enum WhoCanRate
{
    /// <summary>Every member (the default).</summary>
    AllUsers,

    /// <summary>Only the users in <see cref="PluginConfiguration.RaterUserIds"/>, possibly one: the requirement's "limited to
    /// single-user in settings". Also what a value this version doesn't know reads as: nobody gains the right to rate.</summary>
    SelectedUsers
}
