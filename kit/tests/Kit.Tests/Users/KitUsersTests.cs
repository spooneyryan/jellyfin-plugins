using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Kit.Users;
using MediaBrowser.Controller.Library;
using Moq;
using Xunit;
using Jellyfin.Plugin.Kit.TestSupport;

namespace Jellyfin.Plugin.Kit.Tests.Users;

/// <summary>
/// <see cref="KitUsers.GetAll"/> reads the user list through <c>GetUsers()</c> where the server's
/// <see cref="IUserManager"/> has it (10.11.9 and later) and through the <c>Users</c> property otherwise (10.11.5),
/// against fake interfaces of both shapes.
/// </summary>
public sealed class KitUsersTests
{
    /// <summary>The shape of 10.11.9 and later (<c>v10.11.9:MediaBrowser.Controller/Library/IUserManager.cs:28</c>).</summary>
    public interface IUserManager1011_9
    {
        IEnumerable<User> GetUsers();

        IEnumerable<Guid> GetUsersIds();
    }

    /// <summary>The shape of 10.11.5.</summary>
    public interface IUserManager1011_5
    {
        IEnumerable<User> Users { get; }

        IEnumerable<Guid> UsersIds { get; }
    }

    /// <summary>Both members: the method wins.</summary>
    public interface IUserManagerWithBoth
    {
        IEnumerable<User> Users { get; }

        IEnumerable<User> GetUsers();
    }

    /// <summary>A later shape that inherits the method from a base interface.</summary>
    public interface IUserManagerDerived : IUserManager1011_9
    {
        User? GetFirstUser();
    }

    /// <summary>Neither member.</summary>
    public interface IUserManagerWithNeither
    {
        User? GetUserById(Guid id);
    }

    [Fact]
    public void The1011_9Shape_IsReadThroughGetUsers()
    {
        var users = Users("alex", "viewer");
        var manager = new Mock<IUserManager1011_9>(MockBehavior.Strict);
        manager.Setup(m => m.GetUsers()).Returns(users);

        Assert.Equal(KitUsers.GetUsersMethod, KitUsers.MemberUsed<IUserManager1011_9>());
        Assert.Equal(users, KitUsers.GetAll(manager.Object));
        manager.Verify(m => m.GetUsers(), Times.Once);
    }

    [Fact]
    public void The1011_5Shape_IsReadThroughTheUsersProperty()
    {
        var users = Users("alex");
        var manager = new Mock<IUserManager1011_5>(MockBehavior.Strict);
        manager.SetupGet(m => m.Users).Returns(users);

        Assert.Equal(KitUsers.UsersProperty, KitUsers.MemberUsed<IUserManager1011_5>());
        Assert.Equal(users, KitUsers.GetAll(manager.Object));
    }

    [Fact]
    public void WithBothMembers_GetUsersWins()
    {
        var fromMethod = Users("method");
        var manager = new Mock<IUserManagerWithBoth>(MockBehavior.Strict);
        manager.Setup(m => m.GetUsers()).Returns(fromMethod);

        Assert.Equal(KitUsers.GetUsersMethod, KitUsers.MemberUsed<IUserManagerWithBoth>());
        Assert.Equal(fromMethod, KitUsers.GetAll(manager.Object));
        manager.VerifyGet(m => m.Users, Times.Never);
    }

    [Fact]
    public void AMethodOfABaseInterface_IsFound()
    {
        var users = Users("alex");
        var manager = new Mock<IUserManagerDerived>();
        manager.Setup(m => m.GetUsers()).Returns(users);

        Assert.Equal(users, KitUsers.GetAll(manager.Object));
    }

    [Fact]
    public void WithNeitherMember_ItSaysSo()
    {
        var manager = new Mock<IUserManagerWithNeither>();

        Assert.Null(KitUsers.MemberUsed<IUserManagerWithNeither>());
        var ex = Assert.Throws<InvalidOperationException>(() => KitUsers.GetAll(manager.Object));
        Assert.Contains("GetUsers()", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Users", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheServersIUserManager_IsReadThroughTheMemberItsLineHas()
    {
        // The Jellyfin.Controller package the kit compiles against: 10.11.5's Users property, 12's GetUsers().
        Assert.Equal(JellyfinLine.Is12 ? KitUsers.GetUsersMethod : KitUsers.UsersProperty, KitUsers.MemberUsed<IUserManager>());
        var users = Users("admin", "viewer", "viewer2");
        var manager = new Mock<IUserManager>();
        manager.SetupUsers(users);

        Assert.Equal(users, KitUsers.GetAll(manager.Object));
    }

    [Fact]
    public void TheList_IsACopy_WithoutNulls()
    {
        var users = new List<User> { Users("a")[0] };
        var manager = new Mock<IUserManager1011_9>();
        manager.Setup(m => m.GetUsers()).Returns(() => users.Concat([null!]));

        var list = KitUsers.GetAll(manager.Object);
        users.Add(Users("b")[0]);

        Assert.Single(list);
        Assert.Empty(KitUsers.GetAll(new Mock<IUserManager1011_9>().Object));
    }

    [Fact]
    public void Find_NeverAsksForTheEmptyId()
    {
        var user = Users("alex")[0];
        var manager = new Mock<IUserManager>(MockBehavior.Strict);
        manager.Setup(m => m.GetUserById(user.Id)).Returns(user);

        Assert.Null(KitUsers.Find(manager.Object, Guid.Empty));
        Assert.Same(user, KitUsers.Find(manager.Object, user.Id));
    }

    internal static List<User> Users(params string[] names)
        => names.Select(n => new User(n, "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider", "Jellyfin.Server.Implementations.Users.DefaultPasswordResetProvider")).ToList();
}
