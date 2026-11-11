using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.AccessRoles.Access;
using Jellyfin.Plugin.AccessRoles.Audit;
using Jellyfin.Plugin.AccessRoles.Configuration;
using Jellyfin.Plugin.AccessRoles.Policy;
using Jellyfin.Plugin.AccessRoles.Storage;
using Jellyfin.Plugin.AccessRoles.Sync;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.Time;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AccessRoles.Tests.TestSupport;

/// <summary>
/// The library sync over real parts and the 10.11.5 user manager's double: the JSON store and the audit log in a temporary
/// folder, <see cref="FakeJellyfinUsers"/>, <see cref="PolicyWriter"/>, a library catalog the test changes, the settings and
/// a manual clock. Every library and user is synthetic (<see cref="AccessFixtures"/>).
/// </summary>
internal sealed class SyncHarness : IDisposable
{
    private readonly TempDirectory _directory = new();

    public SyncHarness()
    {
        Store = AccessRolesStore.Create(_directory.Combine("accessroles.json"), NullLogger.Instance, Clock);
        Audit = new AccessRolesAuditLog(_directory.Combine("audit"), () => 12, NullLogger.Instance, Clock);
        Paths = AccessRolesPaths.ForRoot(_directory.Path);
        Writer = new PolicyWriter(Server.Manager.Object, Locks, NullLogger.Instance, Clock, TimeSpan.FromMilliseconds(50), TimeSpan.Zero, Server.Committed, TimeSpan.Zero);
        var configuration = new FakeConfigurationProvider(() => Configuration);
        Mirror = new PreferenceMirrorSync(Store, Catalog, new JellyfinServerUsers(Server.Manager.Object), Writer, Audit, configuration, Paths, NullLogger.Instance, Clock);
        Sync = new LibrarySync(Store, Catalog, new JellyfinServerUsers(Server.Manager.Object), Writer, Audit, configuration, Paths, Status, NullLogger.Instance, Clock, Mirror);
    }

    public ManualTimerProvider Clock { get; } = new(new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero));

    public FakeJellyfinUsers Server { get; } = new();

    public UserPolicyLocks Locks { get; } = new();

    public FakeCatalogSource Catalog { get; } = new([.. AccessFixtures.Catalog.Libraries]);

    public PluginConfiguration Configuration { get; } = new();

    public LibrarySyncStatus Status { get; } = new();

    public AccessRolesStore Store { get; }

    public AccessRolesAuditLog Audit { get; }

    public AccessRolesPaths Paths { get; }

    public PolicyWriter Writer { get; }

    public LibrarySync Sync { get; }

    /// <summary>Gets the optional native mirror, which each real run of <see cref="Sync"/> runs after its library pass.</summary>
    public PreferenceMirrorSync Mirror { get; }

    public DateTime Now => Clock.GetUtcNow().UtcDateTime;

    /// <summary>Gets a user as the resolver sees them now (the user manager's cached object).</summary>
    /// <param name="user">The user.</param>
    /// <returns>The server user.</returns>
    public static ServerUser Read(User user) => JellyfinServerUsers.From(user);

    /// <summary>Replaces the stored document (as a save of the roles API would leave it), without starting a run.</summary>
    /// <param name="change">The change.</param>
    /// <returns>A task.</returns>
    public Task SaveAsync(Func<AccessRolesDocument, AccessRolesDocument> change)
        => Store.UpdateAsync(d => change(d) with { }, TestContext.Current.CancellationToken);

    /// <summary>Runs the sync.</summary>
    /// <param name="trigger">Why.</param>
    /// <param name="configure">Changes to the request.</param>
    /// <returns>The report.</returns>
    public Task<SyncReport> RunAsync(SyncTrigger trigger = SyncTrigger.Interval, Func<SyncRequest, SyncRequest>? configure = null)
        => Sync.RunAsync((configure ?? (r => r))(new SyncRequest(trigger, AuditActor.System)), TestContext.Current.CancellationToken);

    /// <summary>Gets the audit lines written, oldest first.</summary>
    /// <returns>The lines.</returns>
    public IReadOnlyList<AuditEntry> Lines() => [.. Audit.Read(0, 200, null, null, null).Items.Reverse()];

    /// <summary>Gets the audit lines of one action, oldest first.</summary>
    /// <param name="action">The action.</param>
    /// <returns>The lines.</returns>
    public IReadOnlyList<AuditEntry> Lines(string action) => [.. Lines().Where(l => l.Action == action)];

    /// <summary>Gets a detail of an audit line as text.</summary>
    /// <param name="entry">The line.</param>
    /// <param name="key">The detail.</param>
    /// <returns>The value's text, or <c>null</c>.</returns>
    public static string? Detail(AuditEntry entry, string key)
        => entry.Details is { } details && details.TryGetValue(key, out var value) && value is not null
            ? value is JsonElement json ? (json.ValueKind == JsonValueKind.String ? json.GetString() : json.GetRawText()) : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)
            : null;

    public void Dispose()
    {
        Sync.Dispose();
        Mirror.Dispose();
        Audit.Dispose();
        Store.Dispose();
        _directory.Dispose();
    }

    /// <summary>
    /// The library catalog of the test: the libraries <c>GetVirtualFolders()</c> would list, and the core's id of a name.
    /// </summary>
    internal sealed class FakeCatalogSource(List<CatalogLibrary> libraries) : ILibraryCatalogSource
    {
        public List<CatalogLibrary> Libraries { get; } = libraries;

        public LibraryCatalog Read() => new(Libraries);

        public Guid IdOf(string name) => Libraries.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.Ordinal))?.Id
            ?? new Guid(MD5.HashData(Encoding.UTF8.GetBytes("Kids:" + name)));
    }
}
