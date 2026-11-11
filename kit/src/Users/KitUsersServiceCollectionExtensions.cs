using System;
using System.IO;
using System.Linq;
using Jellyfin.Data.Events.Users;
using Jellyfin.Plugin.Kit.Diagnostics;
using Jellyfin.Plugin.Kit.Hosting;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kit.Users;

/// <summary>
/// Registers the kit's users services.
/// </summary>
internal static class KitUsersServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="UserReconciler"/> (with the orphan marks in
    /// <c>&lt;data&gt;/&lt;folder&gt;/kit-users.json</c>, logging under <c>Jellyfin.Plugin.&lt;X&gt;.Kit.Users</c>),
    /// <see cref="KitUserDeletedConsumer"/> as a scoped <c>IEventConsumer&lt;UserDeletedEventArgs&gt;</c>, the
    /// <c>Users</c> entry of Diagnostics (with <c>Unavailable</c>, the owners that can't be created), and the
    /// reconciliation as a step of the daily maintenance (<see cref="KitMaintenanceOrder.Users"/>). The plugin adds
    /// each of its <see cref="IUserDataOwner"/>s with <see cref="AddKitUserDataOwner{TOwner}"/> and sets
    /// <see cref="KitOptions.DeletedUserPolicy"/>. Call it after <c>AddKit</c> (and <c>AddKitDiagnostics</c>), only
    /// when <c>KitAbiGuard.Register</c> returned <c>true</c>. It only adds descriptors, so it never throws in
    /// <c>RegisterServices</c>.
    /// </summary>
    /// <param name="services">The server's service collection.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddKitUsers(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (services.Any(d => d.ServiceType == typeof(UserReconciler)))
        {
            return services;
        }

        services.TryAddSingleton(static provider =>
        {
            var options = provider.GetRequiredService<KitOptions>();
            return new KitUserMarks(
                Path.Combine(provider.GetRequiredService<KitPaths>().Root, KitUsersDocument.FileName),
                options.CreateLogger(provider.GetRequiredService<ILoggerFactory>(), "Users"),
                recoveryPage: options.UsersRecoveryPage);
        });
        services.TryAddSingleton(static provider =>
        {
            var options = provider.GetRequiredService<KitOptions>();
            var userManager = provider.GetRequiredService<IUserManager>();
            var logger = options.CreateLogger(provider.GetRequiredService<ILoggerFactory>(), "Users");

            // Each owner created on its own at every use: one that can't be created is left out like a read-only owner,
            // logged once, and tried again next time; the others go on working.
            return new UserReconciler(
                () => KitUsers.GetAll(userManager).GroupBy(u => u.Id).ToDictionary(g => g.Key, g => g.First().Username),
                () => KitComponents.CreateAll<IUserDataOwner>(provider, logger),
                () => KitComponents.Problems<IUserDataOwner>(provider).Select(p => (string)p["Name"]!).ToList(),
                provider.GetRequiredService<KitUserMarks>(),
                () => options.DeletedUserPolicy(provider),
                logger);
        });
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IEventConsumer<UserDeletedEventArgs>, KitUserDeletedConsumer>());
        services.AddKitDiagnosticsSource("Users", static provider =>
        {
            var described = provider.GetRequiredService<UserReconciler>().Describe();
            described["Unavailable"] = KitComponents.Problems<IUserDataOwner>(provider);
            return described;
        });

        // The daily maintenance's user reconciliation, run by AddKitMaintenance's task.
        services.AddKitMaintenanceStep(
            "Users",
            KitMaintenanceOrder.Users,
            static async (provider, cancellationToken) => (await provider.GetRequiredService<UserReconciler>().ReconcileAsync(cancellationToken).ConfigureAwait(false)).ToDictionary());
        return services;
    }

    /// <summary>
    /// Adds a store of user-owned records (created one by one), a singleton of type
    /// <typeparamref name="TOwner"/> the plugin registers itself, to <see cref="AddKitUsers"/>'s reconciler and deleted-user
    /// consumer. The kit creates each owner on its own (<see cref="KitComponent{T}"/>): one whose creation throws is logged,
    /// left out like a read-only owner and tried again at its next use, and every other owner goes on working. Owners
    /// registered as <see cref="IUserDataOwner"/> directly are not seen (Repo.Tests <c>KitAdoptionTests</c> refuses them).
    /// Adding the same type again adds nothing. It only adds descriptors.
    /// </summary>
    /// <typeparam name="TOwner">The owner's type, registered as a singleton by the plugin.</typeparam>
    /// <param name="services">The server's service collection.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddKitUserDataOwner<TOwner>(this IServiceCollection services)
        where TOwner : class, IUserDataOwner
    {
        ArgumentNullException.ThrowIfNull(services);
        var name = typeof(TOwner).FullName ?? typeof(TOwner).Name;
        if (!services.Any(d => d.ImplementationInstance is KitComponent<IUserDataOwner> c && c.Name == name))
        {
            services.AddSingleton(new KitComponent<IUserDataOwner>(name, static provider => provider.GetRequiredService<TOwner>()));
        }

        return services;
    }
}
