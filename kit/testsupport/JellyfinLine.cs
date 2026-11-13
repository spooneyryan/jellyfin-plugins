using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Moq;

namespace Jellyfin.Plugin.Kit.TestSupport;

/// <summary>
/// Test helpers for what differs between the Jellyfin lines a test project is built for (<c>JellyfinTarget</c> 10.11 or
/// 12): the user list of <see cref="IUserManager"/> (10.11's <c>Users</c> property, 12's <c>GetUsers()</c>), a video's
/// <c>PrimaryVersionId</c> (the "N" text on 10.11, a <c>Guid?</c> on 12) and linked children (12 keeps only the item id).
/// </summary>
internal static class JellyfinLine
{
    /// <summary>Gets a value indicating whether the test assembly is built for Jellyfin 12.</summary>
    public static bool Is12 =>
#if JELLYFIN_12
        true;
#else
        false;
#endif

    /// <summary>Gets the server version the build's ABI guard accepts, for registrator tests.</summary>
    public static string ServerVersion => Is12 ? "12.4.0" : "10.11.5";

    /// <summary>Gets the file that describes the build of a plugin for this line: build.12.yaml on 12, else build.yaml.</summary>
    public static string BuildYamlName => Is12 ? "build.12.yaml" : "build.yaml";

    /// <summary>
    /// Gets the version of the plugin's build: its assembly version, the build.yaml or build.12.yaml version of the line.
    /// </summary>
    /// <param name="pluginType">A type of the plugin's assembly.</param>
    /// <returns>The version, such as 1.2.2.0.</returns>
    public static string VersionOf(Type pluginType)
    {
        ArgumentNullException.ThrowIfNull(pluginType);
        return pluginType.Assembly.GetName().Version!.ToString();
    }

    /// <summary>Gets a later patch release of the build's line, which the ABI guard accepts too.</summary>
    public static string LaterPatchVersion => Is12 ? "12.4.11" : "10.11.11";

    /// <summary>Gets <see cref="ServerVersion"/> as the application host reports it.</summary>
    public static Version Server => Version.Parse(ServerVersion);

    /// <summary>Gets a server version the build's ABI guard refuses (the other line).</summary>
    public static string OtherLineVersion => Is12 ? "10.11.5" : "12.1.0";

    /// <summary>Gets the build's Jellyfin line, as the ABI guard names it.</summary>
    public static string Target => Is12 ? "12" : "10.11";

    /// <summary>Gets the other line, as the ABI guard names it.</summary>
    public static string OtherTarget => Is12 ? "10.11" : "12";

    /// <summary>
    /// Makes the mock list these users, through the member the server's line has.
    /// </summary>
    /// <param name="manager">The mock.</param>
    /// <param name="users">The users.</param>
    public static void SetupUsers(this Mock<IUserManager> manager, IEnumerable<User> users)
    {
        ArgumentNullException.ThrowIfNull(manager);
#if JELLYFIN_12
        manager.Setup(m => m.GetUsers()).Returns(users);
#else
        manager.SetupGet(m => m.Users).Returns(users);
#endif
    }

    /// <summary>
    /// Makes the mock list the users the function returns, read again at each call.
    /// </summary>
    /// <param name="manager">The mock.</param>
    /// <param name="users">The users.</param>
    public static void SetupUsers(this Mock<IUserManager> manager, Func<IEnumerable<User>> users)
    {
        ArgumentNullException.ThrowIfNull(manager);
#if JELLYFIN_12
        manager.Setup(m => m.GetUsers()).Returns(users);
#else
        manager.SetupGet(m => m.Users).Returns(users);
#endif
    }

    /// <summary>
    /// Lists the manager's users through the member the server's line has.
    /// </summary>
    /// <param name="manager">The user manager.</param>
    /// <returns>The users.</returns>
    public static IEnumerable<User> AllUsers(this IUserManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
#if JELLYFIN_12
        return manager.GetUsers();
#else
        return manager.Users;
#endif
    }

    /// <summary>
    /// Sets a video's primary version, as merging versions does on the build's line.
    /// </summary>
    /// <param name="video">The video.</param>
    /// <param name="primary">The primary's id, or null for none.</param>
    public static void SetPrimaryVersion(this Video video, Guid? primary)
    {
        ArgumentNullException.ThrowIfNull(video);
#if JELLYFIN_12
        video.SetPrimaryVersionId(primary);
#else
        video.SetPrimaryVersionId(primary?.ToString("N", CultureInfo.InvariantCulture));
#endif
    }

#if JELLYFIN_12
    /// <summary>
    /// The value <c>Video.PrimaryVersionId</c> holds for a primary: on 12 the id itself.
    /// </summary>
    /// <param name="primary">The primary's id.</param>
    /// <returns>The value.</returns>
    public static Guid? VersionId(Guid primary) => primary;

    /// <summary>
    /// The value <c>Video.PrimaryVersionId</c> holds for 10.11's stored text: on 12 the parsed id, or null.
    /// </summary>
    /// <param name="text">The "N" text, or null.</param>
    /// <returns>The value.</returns>
    public static Guid? VersionIdFromText(string? text) => Guid.TryParse(text, out var id) ? id : null;
#else
    /// <summary>
    /// The value <c>Video.PrimaryVersionId</c> holds for a primary: on 10.11 its "N" text.
    /// </summary>
    /// <param name="primary">The primary's id.</param>
    /// <returns>The value.</returns>
    public static string VersionId(Guid primary) => primary.ToString("N", CultureInfo.InvariantCulture);

    /// <summary>
    /// The value <c>Video.PrimaryVersionId</c> holds for 10.11's stored text: the text itself.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>The value.</returns>
    public static string? VersionIdFromText(string? text) => text;
#endif

    /// <summary>
    /// Gets a video's primary version id as 10.11 stores it, the "N" text, or null.
    /// </summary>
    /// <param name="video">The video.</param>
    /// <returns>The text, or null.</returns>
    public static string? PrimaryVersionText(this Video video)
    {
        ArgumentNullException.ThrowIfNull(video);
#if JELLYFIN_12
        return video.PrimaryVersionId is { } id && id != Guid.Empty ? id.ToString("N", CultureInfo.InvariantCulture) : null;
#else
        return string.IsNullOrEmpty(video.PrimaryVersionId) ? null : video.PrimaryVersionId;
#endif
    }

    /// <summary>
    /// A linked child: on 10.11 with the path (when given), on 12 the item id only, as Jellyfin 12 stores links.
    /// </summary>
    /// <param name="itemId">The linked item's id.</param>
    /// <param name="path">The linked item's path (10.11 only).</param>
    /// <returns>The link.</returns>
    public static LinkedChild Link(Guid? itemId, string? path = null)
    {
#if JELLYFIN_12
        _ = path;
        return new LinkedChild { ItemId = itemId };
#else
        return new LinkedChild { ItemId = itemId, Path = path };
#endif
    }

    /// <summary>
    /// Records an extra with its owner, as a scan does: on 10.11 the owner's <c>ExtraIds</c> list; on 12, which keeps no
    /// list, nothing (the extra's <c>OwnerId</c> and <c>ExtraType</c> are what <c>GetExtras</c> queries).
    /// </summary>
    /// <param name="owner">The owner.</param>
    /// <param name="extraId">The extra's id.</param>
    public static void AddExtraId(this BaseItem owner, Guid extraId)
    {
        ArgumentNullException.ThrowIfNull(owner);
#if JELLYFIN_12
        _ = extraId;
#else
        owner.ExtraIds = [.. owner.ExtraIds ?? [], extraId];
#endif
    }

    /// <summary>
    /// A linked child's path on 10.11; null on 12, whose links carry none.
    /// </summary>
    /// <param name="link">The link.</param>
    /// <returns>The path, or null.</returns>
    public static string? LinkPath(this LinkedChild link)
    {
        ArgumentNullException.ThrowIfNull(link);
#if JELLYFIN_12
        return null;
#else
        return link.Path;
#endif
    }
}
