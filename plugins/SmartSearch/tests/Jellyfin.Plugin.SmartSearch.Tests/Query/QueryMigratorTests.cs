using System;
using System.Linq;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.SmartSearch.Query;
using Jellyfin.Plugin.SmartSearch.Query.Migration;
using Xunit;

namespace Jellyfin.Plugin.SmartSearch.Tests.Query;

/// <summary>
/// A v1 round trip, <c>Extra</c> preserved, a future version marked unsupported; and the step
/// chain the migrator runs once there is a version 2.
/// </summary>
public sealed class QueryMigratorTests
{
    private const string V1 = """
        {
          "Version": 1,
          "Scope": { "ItemKinds": ["Movie", "Episode"], "LibraryIds": ["f137a2dd21bbc1b99aa5c0f6bf02a805"], "IncludeMissing": true, "ScopeHint": "kept" },
          "Where": { "Match": "Any", "Negate": true, "Children": [
              { "Field": "Genres", "Op": "AnyOf", "Values": ["Horror", "Thriller"], "Weight": 2 },
              { "Children": [ { "Field": "Year", "Op": "Between", "From": 1980, "To": 1989 } ], "Collapsed": true },
              { "Field": "Title", "Op": "Contains", "Value": "part", "IncludeOriginalTitle": true },
              { "Field": "PremiereDate", "Op": "InLast", "Amount": 90, "Unit": "Weeks" } ] },
          "Sort": [ { "Key": "PremiereDate", "Order": "Descending", "Nulls": "Last" }, { "Key": "SortName" } ],
          "Limit": 250,
          "Future": { "Nested": [1, 2, 3] }
        }
        """;

    [Fact]
    public void V1_RoundTrips_WithExtraPreserved()
    {
        var first = TestCatalog.Read(V1);
        var written = QueryJson.Write(first);
        var second = TestCatalog.Read(written);

        Assert.Equal(written, QueryJson.Write(second));
        Assert.Equal(TestCatalog.Canonicalizer.Canonicalize(first), TestCatalog.Canonicalizer.Canonicalize(second));

        // Unknown properties at every level come back as they were.
        Assert.Equal("{\"Nested\":[1,2,3]}", second.Extra!["Future"].GetRawText().Replace(" ", string.Empty, StringComparison.Ordinal));
        Assert.Equal("kept", second.Scope.Extra!["ScopeHint"].GetString());
        Assert.Equal(2, second.Where.Children![0].Extra!["Weight"].GetInt32());
        Assert.True(second.Where.Children[1].Extra!["Collapsed"].GetBoolean());
        Assert.Equal("Last", second.Sort[0].Extra!["Nulls"].GetString());

        // ...and a query of this version may not carry them: each is UnknownProperty.
        Assert.Equal(
            ["Future:UnknownProperty", "Scope.ScopeHint:UnknownProperty", "Where.Children[0].Weight:UnknownProperty", "Where.Children[1].Collapsed:UnknownProperty", "Sort[0].Nulls:UnknownProperty"],
            TestCatalog.Validator.Validate(second, Jellyfin.Plugin.SmartSearch.Query.Validation.QueryValidationContext.User).Errors.Select(e => e.Path + ":" + e.Code));
    }

    // A newer query's unknown properties are expected: it is unsupported as a whole, and its JSON is kept as it was.
    [Fact]
    public void ANewerQuery_IsUnsupported_NotUnknownProperties()
    {
        var newer = TestCatalog.Read(V1);
        newer.Version = 2;
        Assert.Equal(["Version:UnsupportedVersion"], TestCatalog.Validator.Validate(newer, Jellyfin.Plugin.SmartSearch.Query.Validation.QueryValidationContext.User).Errors.Select(e => e.Path + ":" + e.Code));
        var read = QueryJson.Read(V1.Replace("\"Version\": 1", "\"Version\": 2", StringComparison.Ordinal));
        Assert.Equal(QueryMigrationStatus.Newer, read.Migration);
        Assert.Null(read.Definition);
    }

    [Fact]
    public void Extra_DoesNotChangeTheHash()
        => Assert.Equal(TestCatalog.Hash(V1), TestCatalog.Hash(V1.Replace("\"Weight\": 2", "\"Weight\": 3", StringComparison.Ordinal).Replace("\"kept\"", "\"other\"", StringComparison.Ordinal)));

    [Fact]
    public void AMissingVersion_IsVersion1()
    {
        var root = (JsonObject)JsonNode.Parse("{\"Limit\":3}")!;
        Assert.Equal(new QueryMigrationResult(QueryMigrationStatus.UpToDate, 1), QueryMigrator.Default.Migrate(root));
        Assert.Equal(1, TestCatalog.Read("{\"Limit\":3}").Version);
    }

    [Theory]
    [InlineData("2")]
    [InlineData("99")]
    public void AFutureVersion_IsUnsupported_AndLeftAlone(string version)
    {
        var json = "{\"Version\":" + version + ",\"Where\":{\"Field\":\"Anything\",\"Op\":\"FromTheFuture\"},\"NewThing\":[1]}";
        var root = (JsonObject)JsonNode.Parse(json)!;
        var before = root.ToJsonString();
        Assert.Equal(new QueryMigrationResult(QueryMigrationStatus.Newer, int.Parse(version, System.Globalization.CultureInfo.InvariantCulture)), QueryMigrator.Default.Migrate(root));
        Assert.Equal(before, root.ToJsonString());

        var read = QueryJson.Read(json);
        Assert.Null(read.Definition);
        Assert.False(read.Supported);
        Assert.Equal(QueryMigrationStatus.Newer, read.Migration);
        Assert.Equal(["Version:UnsupportedVersion"], read.Errors.Select(e => e.Path + ":" + e.Code));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("1.0")]
    [InlineData("1e0")]
    [InlineData("-2147483649")]
    [InlineData("\"1\"")]
    [InlineData("null")]
    [InlineData("true")]
    public void AVersionThatIsntAWholeNumberFrom1_IsInvalid(string version)
    {
        var read = QueryJson.Read("{\"Version\":" + version + "}");
        Assert.Null(read.Definition);
        Assert.True(read.Supported);
        Assert.Equal(QueryMigrationStatus.Invalid, read.Migration);
        Assert.Equal(["Version:InvalidVersion"], read.Errors.Select(e => e.Path + ":" + e.Code));
        Assert.Contains("not 1.0", read.Errors[0].Message, StringComparison.Ordinal);
    }

    // A version past int's range is a newer one, not invalid JSON.
    [Theory]
    [InlineData("2147483648")]
    [InlineData("99999999999999999999999999999999999999")]
    public void AVersionTooLargeForAnInt_IsNewer(string version)
    {
        var read = QueryJson.Read("{\"Version\":" + version + ",\"Where\":{\"Op\":\"FromTheFuture\"}}");
        Assert.Null(read.Definition);
        Assert.Equal(QueryMigrationStatus.Newer, read.Migration);
        Assert.Equal(["Version:UnsupportedVersion"], read.Errors.Select(e => e.Path + ":" + e.Code));
    }

    [Fact]
    public void Steps_RunInOrder_UpToTheCurrentVersion_KeepingWhatTheyDontKnow()
    {
        var migrator = new QueryMigrator([new RenameField(2, "Rating", "CommunityRating"), new RenameField(1, "Name", "Rating")], 3);
        var root = (JsonObject)JsonNode.Parse("{\"version\":1,\"Where\":{\"Children\":[{\"Field\":\"Name\",\"Op\":\"Ge\",\"Value\":7}]},\"Extra\":\"kept\"}", new JsonNodeOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal(new QueryMigrationResult(QueryMigrationStatus.Migrated, 1), migrator.Migrate(root));
        Assert.Equal("{\"Where\":{\"Children\":[{\"Field\":\"CommunityRating\",\"Op\":\"Ge\",\"Value\":7}]},\"Extra\":\"kept\",\"Version\":3}", root.ToJsonString());

        var fromTwo = (JsonObject)JsonNode.Parse("{\"Version\":2,\"Where\":{\"Children\":[{\"Field\":\"Rating\"}]}}")!;
        Assert.Equal(new QueryMigrationResult(QueryMigrationStatus.Migrated, 2), migrator.Migrate(fromTwo));
        Assert.Equal("{\"Where\":{\"Children\":[{\"Field\":\"CommunityRating\"}]},\"Version\":3}", fromTwo.ToJsonString());

        var current = (JsonObject)JsonNode.Parse("{\"Version\":3,\"Where\":{\"Children\":[{\"Field\":\"Rating\"}]}}")!;
        Assert.Equal(QueryMigrationStatus.UpToDate, migrator.Migrate(current).Status);
        Assert.Contains("\"Rating\"", current.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheStepChain_IsComplete()
    {
        Assert.Throws<ArgumentException>(() => new QueryMigrator([new RenameField(1, "a", "b")], 3)); // no step from 2
        Assert.Throws<ArgumentException>(() => new QueryMigrator([new RenameField(1, "a", "b"), new RenameField(1, "b", "c")], 2)); // repeats
        Assert.Throws<ArgumentException>(() => new QueryMigrator([new RenameField(2, "a", "b")], 2)); // past the current version
        Assert.Throws<ArgumentException>(() => new QueryMigrator([new RenameField(0, "a", "b")], 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new QueryMigrator([], 0));
        Assert.Equal(QueryMigrator.CurrentVersion, QueryMigrator.Default.Current);
        Assert.Equal(1, QueryMigrator.CurrentVersion);
    }

    // A test step: renames a field id in every rule (the kind of change a migration is for, though a rename normally keeps an alias).
    private sealed class RenameField(int fromVersion, string from, string to) : IQueryMigration
    {
        public int FromVersion { get; } = fromVersion;

        public void Apply(JsonObject query) => Rename(query["Where"] as JsonObject);

        private void Rename(JsonObject? node)
        {
            if (node is null)
            {
                return;
            }

            if (node["Field"]?.GetValue<string>() == from)
            {
                node["Field"] = to;
            }

            foreach (var child in (node["Children"] as JsonArray ?? []).OfType<JsonObject>())
            {
                Rename(child);
            }
        }
    }
}
