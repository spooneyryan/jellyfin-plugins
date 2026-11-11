using System;
using System.Collections.Generic;
using System.Text.Json;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.Kit.Users;
using Xunit;

namespace Jellyfin.Plugin.Kit.Tests.Users;

/// <summary>
/// <see cref="KnownUsers"/>, the last seen names a store keeps for <i>Remap by name</i> and the export's
/// <c>Users[]</c>.
/// </summary>
public sealed class KnownUsersTests
{
    private static readonly Guid Alex = Guid.Parse("5f0c4a1e-0000-4000-8000-000000000001");
    private static readonly Guid Viewer = Guid.Parse("0a0c4a1e-0000-4000-8000-000000000002");
    private static readonly Guid Gone = Guid.Parse("ff0c4a1e-0000-4000-8000-000000000003");

    [Fact]
    public void Json_KeysAreJellyfinsNFormat_InOrdinalOrder_AndReadInAnyFormat()
    {
        var known = KnownUsers.Empty.With(Alex, "alex").With(Viewer, "viewer");

        var json = JsonSerializer.Serialize(new Holder { Known = known }, JsonStoreFormat.Options);

        Assert.Equal(
            "{\n  \"Known\": {\n    \"0a0c4a1e000040008000000000000002\": \"viewer\",\n    \"5f0c4a1e000040008000000000000001\": \"alex\"\n  }\n}",
            json.Replace("\r\n", "\n", StringComparison.Ordinal));
        var read = JsonSerializer.Deserialize<Holder>("{ \"Known\": { \"5f0c4a1e-0000-4000-8000-000000000001\": \"alex\", \"0A0C4A1E000040008000000000000002\": \" viewer \" } }", JsonStoreFormat.Options)!;
        Assert.Equal(known, read.Known);
    }

    [Fact]
    public void Json_DropsEntriesItCantRead_AndNullIsEmpty()
    {
        var read = JsonSerializer.Deserialize<Holder>(
            "{ \"Known\": { \"not a guid\": \"x\", \"5f0c4a1e000040008000000000000001\": 7, \"0a0c4a1e000040008000000000000002\": \"  \", \"ff0c4a1e000040008000000000000003\": \"kept\", \"00000000000000000000000000000000\": \"empty\" } }",
            JsonStoreFormat.Options)!;

        Assert.Equal(KnownUsers.Empty.With(Gone, "kept"), read.Known);
        Assert.Same(KnownUsers.Empty, JsonSerializer.Deserialize<Holder>("{ \"Known\": null }", JsonStoreFormat.Options)!.Known);
        Assert.Same(KnownUsers.Empty, JsonSerializer.Deserialize<Holder>("{ }", JsonStoreFormat.Options)!.Known);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Holder>("{ \"Known\": [] }", JsonStoreFormat.Options));
    }

    [Fact]
    public void With_IgnoresBlankNamesAndEmptyIds_AndReturnsTheSameInstanceWhenNothingChanges()
    {
        var known = KnownUsers.Empty.With(Alex, " alex ");

        Assert.Equal("alex", known.NameOf(Alex));
        Assert.Same(known, known.With(Alex, "alex"));
        Assert.Same(known, known.With(Alex, "  "));
        Assert.Same(known, known.With(Guid.Empty, "nobody"));
        Assert.Equal("Alex", known.With(Alex, "Alex").NameOf(Alex));
        Assert.Null(known.NameOf(Viewer));
        Assert.Same(KnownUsers.Empty, known.Without(Alex));
        Assert.Same(known, known.Without(Viewer));
    }

    [Fact]
    public void Refresh_TakesServerNames_KeepsUnknownIdsNames_AndDropsIdsWithoutRecords()
    {
        var known = KnownUsers.Empty.With(Alex, "old alex").With(Gone, "gone").With(Viewer, "viewer");
        var server = new Dictionary<Guid, string> { [Alex] = "alex", [Viewer] = "viewer" };

        var refreshed = known.Refresh([Alex, Gone], server);

        Assert.Equal(KnownUsers.Empty.With(Alex, "alex").With(Gone, "gone"), refreshed);
        Assert.Same(refreshed, refreshed.Refresh([Alex, Gone], server));
    }

    [Fact]
    public void Remap_MovesNamesToTheNewIds_UnlessTheyHaveOne()
    {
        var newAlex = Guid.NewGuid();
        var known = KnownUsers.Empty.With(Alex, "alex").With(Gone, "gone").With(Viewer, "viewer");

        var remapped = known.Remap(new Dictionary<Guid, Guid> { [Alex] = newAlex, [Gone] = Viewer });

        Assert.Equal(KnownUsers.Empty.With(newAlex, "alex").With(Viewer, "viewer"), remapped);
        Assert.Same(remapped, remapped.Remap(new Dictionary<Guid, Guid> { [Alex] = newAlex }));
    }

    [Fact]
    public void Equality_IsByContent()
    {
        Assert.Equal(KnownUsers.Empty.With(Alex, "a").With(Viewer, "b"), KnownUsers.Empty.With(Viewer, "b").With(Alex, "a"));
        Assert.Equal(
            KnownUsers.Empty.With(Alex, "a").With(Viewer, "b").GetHashCode(),
            KnownUsers.From([new KeyValuePair<Guid, string>(Viewer, "b"), new KeyValuePair<Guid, string>(Alex, "a")]).GetHashCode());
        Assert.NotEqual(KnownUsers.Empty.With(Alex, "a"), KnownUsers.Empty.With(Alex, "A"));
        Assert.Equal("5f0c4a1e000040008000000000000001", KnownUsers.FormatId(Alex));
    }

    private sealed class Holder
    {
        public KnownUsers Known { get; set; } = KnownUsers.Empty;
    }
}
