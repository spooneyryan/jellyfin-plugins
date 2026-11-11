using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.Kit.Users;

/// <summary>
/// The only way a plugin lists the server's users (first needed by Access Roles).
/// </summary>
/// <remarks>
/// <para>Jellyfin 10.11.5 exposes the list as the property <c>IUserManager.Users</c>; 10.11.9 replaced it with the
/// method <c>GetUsers()</c> (and <c>UsersIds</c> with <c>GetUsersIds()</c>;
/// <c>v10.11.9:MediaBrowser.Controller/Library/IUserManager.cs:28</c>). A plugin compiled against 10.11.5 that reads
/// <c>Users</c> directly throws <see cref="MissingMethodException"/> on 10.11.9 and later. <see cref="GetAll"/> looks
/// the member up on the server's own <see cref="IUserManager"/> once, preferring <c>GetUsers()</c> and falling back to
/// the <c>Users</c> property, and calls it through a delegate. Repo.Tests checks that no plugin (kit included) reads
/// <c>IUserManager.Users</c> or <c>UsersIds</c> any other way.</para>
/// <para><c>GetUserById</c> and <c>GetUserByName</c> have the same shape on every 10.11 release; use them directly, or
/// <see cref="Find"/>, which never passes <see cref="Guid.Empty"/> (10.11.5 documents an <see cref="ArgumentException"/>).</para>
/// </remarks>
internal static class KitUsers
{
    /// <summary>The 10.11.9 and later method.</summary>
    public const string GetUsersMethod = "GetUsers";

    /// <summary>The 10.11.5 property.</summary>
    public const string UsersProperty = "Users";

    /// <summary>
    /// Lists the server's users, whichever shape its <see cref="IUserManager"/> has.
    /// </summary>
    /// <param name="userManager">The server's user manager.</param>
    /// <returns>A copy of the list, in the order the server gives.</returns>
    /// <exception cref="InvalidOperationException">The server's <see cref="IUserManager"/> has neither member.</exception>
    public static IReadOnlyList<User> GetAll(IUserManager userManager) => GetAll<IUserManager>(userManager);

    /// <summary>
    /// Finds a user by id, or <c>null</c>; <see cref="Guid.Empty"/> finds nothing without asking the server.
    /// </summary>
    /// <param name="userManager">The server's user manager.</param>
    /// <param name="userId">The user id.</param>
    /// <returns>The user, or <c>null</c>.</returns>
    public static User? Find(IUserManager userManager, Guid userId)
    {
        ArgumentNullException.ThrowIfNull(userManager);
        return userId.Equals(Guid.Empty) ? null : userManager.GetUserById(userId);
    }

    /// <summary>
    /// Lists the users of any user-manager type that has either shape (tests pass fake 10.11.5 and 10.11.9 interfaces).
    /// </summary>
    /// <typeparam name="TManager">The type whose members are looked up: the interface, as the server exposes it.</typeparam>
    /// <param name="userManager">The instance.</param>
    /// <returns>A copy of the list.</returns>
    internal static IReadOnlyList<User> GetAll<TManager>(TManager userManager)
        where TManager : class
    {
        ArgumentNullException.ThrowIfNull(userManager);
        var users = Accessor<TManager>.Read(userManager);
        return users is null ? [] : users.Where(u => u is not null).ToList();
    }

    /// <summary>
    /// Describes which member a user-manager type would be read through: <c>GetUsers()</c> or <c>Users</c>.
    /// </summary>
    /// <typeparam name="TManager">The type.</typeparam>
    /// <returns>The member's name, or <c>null</c> when the type has neither.</returns>
    internal static string? MemberUsed<TManager>()
        where TManager : class
        => Accessor<TManager>.Member;

    // One lookup per type, the first time it is used.
    private static class Accessor<TManager>
        where TManager : class
    {
        private static readonly (Func<TManager, IEnumerable<User>?>? Read, string? Member) _accessor = Create();

        public static string? Member => _accessor.Member;

        public static IEnumerable<User>? Read(TManager manager)
            => (_accessor.Read ?? throw new InvalidOperationException(
                $"{typeof(TManager).FullName} has neither {GetUsersMethod}() nor {UsersProperty}; this Jellyfin version isn't supported by the kit's KitUsers."))(manager);

        private static (Func<TManager, IEnumerable<User>?>? Read, string? Member) Create()
        {
            var type = typeof(TManager);
            var method = FindMethod(type);
            if (method is not null)
            {
                return (Bind(method), GetUsersMethod);
            }

            var getter = FindProperty(type)?.GetGetMethod();
            return getter is not null ? (Bind(getter), UsersProperty) : (null, null);
        }

        private static MethodInfo? FindMethod(Type type)
            => Members(type)
                .Select(t => t.GetMethod(GetUsersMethod, BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes))
                .FirstOrDefault(m => m is not null && typeof(IEnumerable<User>).IsAssignableFrom(m.ReturnType));

        private static PropertyInfo? FindProperty(Type type)
            => Members(type)
                .Select(t => t.GetProperty(UsersProperty, BindingFlags.Public | BindingFlags.Instance))
                .FirstOrDefault(p => p is not null && p.GetIndexParameters().Length == 0 && typeof(IEnumerable<User>).IsAssignableFrom(p.PropertyType));

        // An interface's members don't include its base interfaces' ones.
        private static IEnumerable<Type> Members(Type type) => type.IsInterface ? [type, .. type.GetInterfaces()] : [type];

        private static Func<TManager, IEnumerable<User>?> Bind(MethodInfo method)
        {
            if (method.ReturnType == typeof(IEnumerable<User>) && method.DeclaringType!.IsAssignableFrom(typeof(TManager)))
            {
                try
                {
                    // An open-instance delegate: an interface method dispatches to the implementation.
                    return (Func<TManager, IEnumerable<User>?>)Delegate.CreateDelegate(typeof(Func<TManager, IEnumerable<User>?>), method);
                }
                catch (ArgumentException)
                {
                    // Fall through to reflection.
                }
            }

            return manager => (IEnumerable<User>?)method.Invoke(manager, null);
        }
    }
}
