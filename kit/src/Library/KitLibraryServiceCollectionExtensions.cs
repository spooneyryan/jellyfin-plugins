using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Diagnostics;
using Jellyfin.Plugin.Kit.Hosting;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kit.Library;

/// <summary>
/// Registers the kit's item-identity services, its library-change monitor, and the record lifecycle and library reads:
/// the verify sweep, the detached records with their archive, <see cref="LibraryAccess"/> and
/// <see cref="FreshUserData"/>.
/// </summary>
internal static class KitLibraryServiceCollectionExtensions
{
    /// <summary>The Diagnostics key of the library-change monitor.</summary>
    public const string LibraryChangesDiagnosticsKey = "LibraryChanges";

    /// <summary>The Diagnostics key of the verify sweep.</summary>
    public const string VerifierDiagnosticsKey = "Verifier";

    /// <summary>The Diagnostics key of the detached records (<c>Detached</c>).</summary>
    public const string DetachedDiagnosticsKey = "Detached";

    /// <summary>
    /// Registers <see cref="MoveJournalReader"/> (for <c>&lt;data&gt;/librarymaintenance/moves.jsonl</c>, logging under
    /// <c>Jellyfin.Plugin.&lt;X&gt;.Kit.MoveJournal</c>) and <see cref="IdentityResolver"/> (on
    /// <see cref="KitOptions.Clock"/>, for the pending window), both singletons. Call it after <c>AddKit</c>, only when
    /// <c>KitAbiGuard.Register</c> returned <c>true</c>. It only adds descriptors, so it never throws in
    /// <c>RegisterServices</c>.
    /// </summary>
    /// <param name="services">The server's service collection.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddKitIdentity(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(static provider => new MoveJournalReader(
            provider.GetRequiredService<IApplicationPaths>(),
            provider.GetRequiredService<KitOptions>().CreateLogger(provider.GetRequiredService<ILoggerFactory>(), "MoveJournal")));
        services.TryAddSingleton(static provider => new IdentityResolver(
            provider.GetRequiredService<ILibraryManager>(),
            provider.GetRequiredService<MoveJournalReader>(),
            provider.GetRequiredService<KitOptions>().Clock(provider)));
        return services;
    }

    /// <summary>
    /// Registers <see cref="LibraryChangeMonitor"/> as a singleton and as a hosted service (it subscribes once the startup
    /// gate is open), and its Diagnostics entry <c>LibraryChanges</c> (with <c>Unavailable</c>, the consumers that can't be
    /// created). The plugin adds each of its <see cref="ILibraryChangeConsumer"/>s with
    /// <see cref="AddKitLibraryConsumer{TConsumer}"/> (the monitor creates each on its own when it first needs them, and tries
    /// one that couldn't be created again every minute), and declares a public sealed post-scan task deriving from
    /// <see cref="KitPostScanTaskBase"/> that flushes it inside every scan. Call it after <c>AddKit</c> (and
    /// <c>AddKitDiagnostics</c>), only when <c>KitAbiGuard.Register</c> returned <c>true</c>. It only adds descriptors, so it
    /// never throws in <c>RegisterServices</c>.
    /// </summary>
    /// <param name="services">The server's service collection.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddKitLibraryChanges(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (services.Any(d => d.ServiceType == typeof(LibraryChangeMonitor)))
        {
            return services;
        }

        services.AddSingleton(static provider =>
        {
            var options = provider.GetRequiredService<KitOptions>();
            var loggerFactory = provider.GetRequiredService<ILoggerFactory>();
            var logger = options.CreateLogger(loggerFactory, LibraryChangeMonitor.LoggerComponent);

            // Each consumer created on its own: one that can't be created costs only itself.
            return new LibraryChangeMonitor(
                provider.GetRequiredService<IServerApplicationHost>(),
                provider.GetRequiredService<ILibraryManager>(),
                provider.GetService<IUserDataManager>(),
                () => KitComponents.CreateAll<ILibraryChangeConsumer>(provider, logger),
                options,
                loggerFactory,
                someConsumersUnavailable: () => KitComponents.Problems<ILibraryChangeConsumer>(provider).Count > 0);
        });
        services.AddHostedService(static provider => provider.GetRequiredService<LibraryChangeMonitor>());
        services.AddKitDiagnosticsSource(LibraryChangesDiagnosticsKey, static provider =>
        {
            var described = provider.GetRequiredService<LibraryChangeMonitor>().Describe();
            described["Unavailable"] = KitComponents.Problems<ILibraryChangeConsumer>(provider);
            return described;
        });
        return services;
    }

    /// <summary>
    /// Adds a consumer of library changes (created one by one), a singleton of type
    /// <typeparamref name="TConsumer"/> the plugin registers itself, to <see cref="AddKitLibraryChanges"/>'s monitor.
    /// The monitor creates each consumer on its own (<see cref="KitComponent{T}"/>): one whose creation throws is
    /// logged, left out, and tried again every minute (it then gets the changes from then on); every other consumer
    /// goes on getting its changes. Consumers registered as <see cref="ILibraryChangeConsumer"/> directly are not seen
    /// (Repo.Tests <c>KitAdoptionTests</c> refuses them). Adding the same type again adds nothing. It only adds
    /// descriptors, so it never throws.
    /// </summary>
    /// <typeparam name="TConsumer">The consumer's type, registered as a singleton by the plugin.</typeparam>
    /// <param name="services">The server's service collection.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddKitLibraryConsumer<TConsumer>(this IServiceCollection services)
        where TConsumer : class, ILibraryChangeConsumer
    {
        ArgumentNullException.ThrowIfNull(services);
        var name = typeof(TConsumer).FullName ?? typeof(TConsumer).Name;
        if (!services.Any(d => d.ImplementationInstance is KitComponent<ILibraryChangeConsumer> c && c.Name == name))
        {
            services.AddSingleton(new KitComponent<ILibraryChangeConsumer>(name, static provider => provider.GetRequiredService<TConsumer>()));
        }

        return services;
    }

    /// <summary>
    /// Registers <see cref="LibraryAccess"/> and <see cref="FreshUserData"/>, singletons.
    /// <see cref="FreshUserData"/> reads through <c>GetItemList</c> first and falls back to <c>IItemRepository.RetrieveItem</c>,
    /// logging under <c>Jellyfin.Plugin.&lt;X&gt;.Kit.UserData</c>. It only adds descriptors, so it never throws
    /// in <c>RegisterServices</c>.
    /// </summary>
    /// <param name="services">The server's service collection.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddKitLibraryAccess(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(static provider => new LibraryAccess(provider.GetRequiredService<ILibraryManager>()));
        services.TryAddSingleton(static provider => new FreshUserData(
            provider.GetRequiredService<ILibraryManager>(),
            provider.GetRequiredService<IUserDataManager>(),
            provider.GetService<IItemRepository>(),
            provider.GetRequiredService<KitOptions>().CreateLogger(provider.GetRequiredService<ILoggerFactory>(), "UserData")));
        return services;
    }

    /// <summary>
    /// Registers the verify sweep: <see cref="AttachedRecordVerifier"/> as a singleton and as a hosted service (its
    /// startup sweep runs once after the startup gate, after the archives' reconcile when
    /// <see cref="AddKitDetachedRecords"/> registered them), a daily maintenance step, and its Diagnostics entry
    /// <c>Verifier</c> (with <c>Unavailable</c>, the owners that can't be created). The plugin adds its owners with
    /// <see cref="AddKitRecordOwner{TOwner}"/>; consumers of library changes await
    /// <see cref="AttachedRecordVerifier.SweepAsync"/> for a batch with removed folders. Adds
    /// <see cref="AddKitIdentity"/> (the move journal, for pending relinks). Call it after <c>AddKit</c>, only when
    /// <c>KitAbiGuard.Register</c> returned <c>true</c>; it only adds descriptors, so it never throws.
    /// </summary>
    /// <param name="services">The server's service collection.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddKitVerifier(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (services.Any(d => d.ServiceType == typeof(AttachedRecordVerifier)))
        {
            return services;
        }

        services.AddKitIdentity();
        services.AddSingleton(static provider =>
        {
            var options = provider.GetRequiredService<KitOptions>();
            var logger = options.CreateLogger(provider.GetRequiredService<ILoggerFactory>(), AttachedRecordVerifier.LoggerComponent);
            return new AttachedRecordVerifier(
                provider.GetRequiredService<IServerApplicationHost>(),
                provider.GetRequiredService<ILibraryManager>(),
                provider.GetRequiredService<MoveJournalReader>(),
                () => KitComponents.CreateAll<IAttachedRecordOwner>(provider, logger),
                logger,
                options.Clock(provider),
                cancellationToken => provider.GetService<DetachedRecords>()?.ReconcileAsync(cancellationToken) ?? Task.CompletedTask);
        });
        services.AddHostedService(static provider => provider.GetRequiredService<AttachedRecordVerifier>());
        services.AddKitDiagnosticsSource(VerifierDiagnosticsKey, static provider =>
        {
            var described = provider.GetRequiredService<AttachedRecordVerifier>().Describe();
            described["Unavailable"] = KitComponents.Problems<IAttachedRecordOwner>(provider);
            return described;
        });
        services.AddKitMaintenanceStep(
            "VerifySweep",
            KitMaintenanceOrder.VerifySweep,
            static async (provider, cancellationToken) => (await provider.GetRequiredService<AttachedRecordVerifier>().SweepAsync(VerifySweepReason.Daily, cancellationToken).ConfigureAwait(false)).ToDictionary());
        return services;
    }

    /// <summary>
    /// Registers the kit's handling of detached records: <see cref="DetachedRecords"/> (the
    /// archives in <c>&lt;data&gt;/&lt;folder&gt;/archive</c>, the purge copies in its <c>snapshots/pre-op</c>, logging under
    /// <c>Jellyfin.Plugin.&lt;X&gt;.Kit.Detached</c>), the daily maintenance steps (the retention archive step, the
    /// re-resolution of live detached records, the reattach from the archive), and its Diagnostics entry <c>Detached</c>.
    /// The plugin adds each of its <see cref="IDetachedRecordOwner"/>s with <see cref="AddKitRecordOwner{TOwner}"/> (which
    /// adds it to <see cref="AddKitVerifier"/>'s owners too). Adds <see cref="AddKitIdentity"/>. It only adds descriptors.
    /// </summary>
    /// <param name="services">The server's service collection.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddKitDetachedRecords(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (services.Any(d => d.ServiceType == typeof(DetachedRecords)))
        {
            return services;
        }

        services.AddKitIdentity();
        services.AddSingleton(static provider =>
        {
            var options = provider.GetRequiredService<KitOptions>();
            var paths = provider.GetRequiredService<KitPaths>();
            var userManager = provider.GetRequiredService<IUserManager>();
            var logger = options.CreateLogger(provider.GetRequiredService<ILoggerFactory>(), DetachedRecords.LoggerComponent);
            return new DetachedRecords(
                paths.Archive,
                paths.Snapshots,
                provider.GetRequiredService<ILibraryManager>(),
                id => userManager.GetUserById(id),
                provider.GetRequiredService<IdentityResolver>(),
                provider.GetRequiredService<MoveJournalReader>(),
                () => KitComponents.CreateAll<IDetachedRecordOwner>(provider, logger),
                logger,
                options.Clock(provider));
        });
        services.AddKitDiagnosticsSource(DetachedDiagnosticsKey, static provider => provider.GetRequiredService<DetachedRecords>().Describe());
        services.AddKitMaintenanceStep("Archive", KitMaintenanceOrder.Archive, static (provider, ct) => EachOwnerAsync(provider, "Archive", static (d, o, token) => d.ArchiveDueAsync(o, token), ct));
        services.AddKitMaintenanceStep("ResolveDetached", KitMaintenanceOrder.ResolveDetached, static (provider, ct) => EachOwnerAsync(provider, "ResolveDetached", static (d, o, token) => d.ResolveDetachedAsync(o, token), ct));
        services.AddKitMaintenanceStep("Reattach", KitMaintenanceOrder.Reattach, static (provider, ct) => EachOwnerAsync(provider, "Reattach", static (d, o, token) => d.ReattachFromArchiveAsync(o, token), ct));
        return services;
    }

    /// <summary>
    /// Adds a store of item-linked records, a singleton of type <typeparamref name="TOwner"/> the plugin registers
    /// itself, to the verify sweep's owners and, when it is an <see cref="IDetachedRecordOwner"/>, to the detached
    /// records'. The kit creates each owner on its own (<see cref="KitComponent{T}"/>): one whose creation throws is
    /// logged, left out and tried again at its next use, and every other owner goes on working. Owners registered as
    /// <see cref="IAttachedRecordOwner"/> or <see cref="IDetachedRecordOwner"/> directly are not seen (Repo.Tests
    /// <c>KitAdoptionTests</c> refuses them). Adding the same type again adds nothing. It only adds descriptors, so it
    /// never throws.
    /// </summary>
    /// <typeparam name="TOwner">The owner's type, registered as a singleton by the plugin.</typeparam>
    /// <param name="services">The server's service collection.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddKitRecordOwner<TOwner>(this IServiceCollection services)
        where TOwner : class, IAttachedRecordOwner
    {
        ArgumentNullException.ThrowIfNull(services);
        var name = typeof(TOwner).FullName ?? typeof(TOwner).Name;
        if (services.Any(d => d.ImplementationInstance is KitComponent<IAttachedRecordOwner> c && c.Name == name))
        {
            return services;
        }

        services.AddSingleton(new KitComponent<IAttachedRecordOwner>(name, static provider => provider.GetRequiredService<TOwner>()));
        if (typeof(IDetachedRecordOwner).IsAssignableFrom(typeof(TOwner)))
        {
            services.AddSingleton(new KitComponent<IDetachedRecordOwner>(name, static provider => (IDetachedRecordOwner)provider.GetRequiredService<TOwner>()));
        }

        return services;
    }

    // A maintenance step's run that runs one DetachedRecords step for every owner, each in its own try/catch.
    private static async Task<IDictionary<string, object?>?> EachOwnerAsync(
        IServiceProvider provider,
        string name,
        Func<DetachedRecords, IDetachedRecordOwner, CancellationToken, Task<DetachedRunResult>> step,
        CancellationToken cancellationToken)
    {
        var records = provider.GetRequiredService<DetachedRecords>();
        var results = new List<object?>();
        foreach (var owner in records.Owners)
        {
            try
            {
                results.Add((await step(records, owner, cancellationToken).ConfigureAwait(false)).ToDictionary());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                provider.GetRequiredService<KitOptions>().CreateLogger(provider.GetRequiredService<ILoggerFactory>(), DetachedRecords.LoggerComponent)
                    .LogError(ex, "The maintenance step {Step} failed for {Owner}", name, owner.Name);
                results.Add(new Dictionary<string, object?> { ["Step"] = name, ["Owner"] = owner.Name, ["Error"] = ex.Message });
            }
        }

        return new Dictionary<string, object?> { ["Owners"] = results };
    }
}
