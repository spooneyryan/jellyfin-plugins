using System;
using System.Threading.Tasks;
using Jellyfin.Plugin.AccessRoles.Storage;
using Jellyfin.Plugin.Kit.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.AccessRoles.Tests.Storage;

/// <summary>
/// The store's change notification, the library sync's first trigger: raised after a
/// change that changed the document, not after one that changed nothing, and not for the sync's own bookkeeping.
/// </summary>
public sealed class StoreChangedTests : IDisposable
{
    private readonly TempDirectory _directory = new();

    [Fact]
    public async Task AChange_RaisesChanged_ANoOpAndTheSyncsOwnWritesDont()
    {
        using var store = AccessRolesStore.Create(_directory.Combine("accessroles.json"), NullLogger.Instance);
        var raised = 0;
        store.Changed += (_, _) => raised++;

        await store.UpdateAsync(d => d with { DefaultRoleIds = [Guid.NewGuid()] }, TestContext.Current.CancellationToken);
        await store.UpdateAsync(d => d, TestContext.Current.CancellationToken);
        await store.UpdateAsync(d => d with { DefaultRoleIds = [] }, TestContext.Current.CancellationToken, notify: false);

        Assert.Equal(1, raised);
        Assert.Empty(store.Current.DefaultRoleIds);
    }

    [Fact]
    public async Task AChangeThatThrows_RaisesNothing()
    {
        using var store = AccessRolesStore.Create(_directory.Combine("accessroles.json"), NullLogger.Instance);
        var raised = 0;
        store.Changed += (_, _) => raised++;

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpdateAsync(_ => throw new InvalidOperationException("refused"), TestContext.Current.CancellationToken));

        Assert.Equal(0, raised);
    }

    public void Dispose() => _directory.Dispose();
}
