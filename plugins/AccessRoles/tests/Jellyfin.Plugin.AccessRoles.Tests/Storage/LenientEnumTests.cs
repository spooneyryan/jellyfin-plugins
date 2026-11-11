using System;
using System.Collections.Generic;
using System.Text.Json;
using Jellyfin.Plugin.AccessRoles.Model;
using Jellyfin.Plugin.AccessRoles.Storage;
using Jellyfin.Plugin.Kit.Storage;
using Xunit;

namespace Jellyfin.Plugin.AccessRoles.Tests.Storage;

/// <summary>
/// The store's lenient enum values: a member's name in any case is that member, anything else is kept as
/// written, unknown, and comes back out unchanged.
/// </summary>
public sealed class LenientEnumTests
{
    [Theory]
    [InlineData("Managed", "Managed", true)]
    [InlineData("managed", "Managed", true)]
    [InlineData("EXCLUDED", "Excluded", true)]
    [InlineData("Paused", "Paused", false)]
    [InlineData("1", "1", false)]
    [InlineData("", "", false)]
    [InlineData(" Managed", " Managed", false)]
    public void Parse_KnowsMembersByNameInAnyCase_AndKeepsTheRest(string text, string name, bool known)
    {
        var value = LenientEnum<AccessRolesAssignmentState>.Parse(text);

        Assert.Equal(name, value.Name);
        Assert.Equal(known, value.IsKnown);
        Assert.Equal(known, value.Value.HasValue);
    }

    [Fact]
    public void TheDefault_IsTheZeroMember()
    {
        Assert.Equal("Managed", default(LenientEnum<AccessRolesAssignmentState>).Name);
        Assert.True(default(LenientEnum<AccessRolesUserLibraryMode>).Is(AccessRolesUserLibraryMode.Inherit));
        Assert.True(default(LenientEnum<AccessRolesRoleLibraryMode>).Is(AccessRolesRoleLibraryMode.NotManaged));
    }

    [Fact]
    public void Equality_IsByStoredName()
    {
        LenientEnum<AccessRolesLock> profile = AccessRolesLock.Profile;

        Assert.Equal(profile, LenientEnum<AccessRolesLock>.Parse("PROFILE"));
        Assert.True(profile == LenientEnum<AccessRolesLock>.From(AccessRolesLock.Profile));
        Assert.True(profile != LenientEnum<AccessRolesLock>.Parse("Profiles"));
        Assert.Equal(profile.GetHashCode(), LenientEnum<AccessRolesLock>.Parse("profile").GetHashCode());
        Assert.False(profile.Is(AccessRolesLock.Home));
        Assert.Throws<ArgumentOutOfRangeException>(() => LenientEnum<AccessRolesLock>.From((AccessRolesLock)99));
    }

    [Fact]
    public void Json_WritesTheName_ReadsNumbersAndNullAsUnknown_AndRefusesOtherTokens()
    {
        var options = JsonStoreFormat.Options;

        Assert.Equal("\"Excluded\"", JsonSerializer.Serialize<LenientEnum<AccessRolesAssignmentState>>(AccessRolesAssignmentState.Excluded, options));
        Assert.Equal("\"Paused\"", JsonSerializer.Serialize(JsonSerializer.Deserialize<LenientEnum<AccessRolesAssignmentState>>("\"Paused\"", options), options));
        Assert.Equal("2", JsonSerializer.Deserialize<LenientEnum<AccessRolesAssignmentState>>("2", options).Name);
        Assert.False(JsonSerializer.Deserialize<LenientEnum<AccessRolesAssignmentState>>("2", options).IsKnown);
        Assert.Equal(string.Empty, JsonSerializer.Deserialize<LenientEnum<AccessRolesAssignmentState>>("null", options).Name);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<LenientEnum<AccessRolesAssignmentState>>("true", options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<LenientEnum<AccessRolesAssignmentState>>("[]", options));
    }

    [Fact]
    public void AsADictionaryKey_TheNameIsThePropertyName()
    {
        var options = JsonStoreFormat.Options;
        var map = new Dictionary<LenientEnum<AccessRolesLock>, LenientEnum<AccessRolesLockOverride>>
        {
            [AccessRolesLock.QuickConnect] = AccessRolesLockOverride.Unlock,
            [LenientEnum<AccessRolesLock>.Parse("Downloads")] = LenientEnum<AccessRolesLockOverride>.Parse("Sometimes")
        };

        var json = JsonSerializer.Serialize(map, options);
        var back = JsonSerializer.Deserialize<Dictionary<LenientEnum<AccessRolesLock>, LenientEnum<AccessRolesLockOverride>>>("{\"quickconnect\":\"unlock\",\"Downloads\":\"Sometimes\"}", options)!;

        Assert.Contains("\"QuickConnect\": \"Unlock\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Downloads\": \"Sometimes\"", json, StringComparison.Ordinal);
        Assert.Equal(AccessRolesLockOverride.Unlock, back[AccessRolesLock.QuickConnect].Value);
        Assert.False(back[LenientEnum<AccessRolesLock>.Parse("Downloads")].IsKnown);
    }
}
