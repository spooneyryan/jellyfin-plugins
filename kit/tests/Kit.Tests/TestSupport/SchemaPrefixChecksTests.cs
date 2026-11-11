using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Tests.Fixtures;
using Jellyfin.Plugin.Kit.Tests.Fixtures.Bad;
using Jellyfin.Plugin.Kit.Tests.TestHosting;
using Jellyfin.Plugin.Kit.TestSupport.Api;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.Swagger;
using Xunit;

namespace Jellyfin.Plugin.Kit.Tests.TestSupport;

/// <summary>
/// The schema-prefix walker against every shape a schema can be reached by, and against the schemas Swashbuckle 6.2.3
/// (the version Jellyfin 10.11.5 ships) really creates for the same controllers.
/// </summary>
public sealed class SchemaPrefixChecksTests
{
    private static readonly Type[] BadControllers =
        [typeof(FxNestedController), typeof(FxCoreNameController), typeof(FxRequestController), typeof(FxTwiceController)];

    private static readonly string[] ExpectedGoodIds =
    [
        "FxItemDto", "FxItemStatus", "FxItemPart", "FxItemTagDto", "FxValueDto", "FxItemDtoQueryResult",
        "FxCreateItemRequest", "FxInternalResultDto", "FxSortMode", "FxUploadKind", "FxItemDtoFxPage"
    ];

    [Fact]
    public void Walk_ReachesEverySchemaVisibleShape()
    {
        var reached = SchemaPrefixChecks.FindReachedSchemas([typeof(FxGoodController)], typeof(FxGoodController).Assembly);

        Assert.Equal(ExpectedGoodIds.Order(), reached.Select(r => r.SchemaId).Order());

        // QueryResult<FxItemDto> is the server's generic: checked against the foreign list, but it needs no prefix of ours.
        var queryResult = Assert.Single(reached, r => r.Type == typeof(QueryResult<FxItemDto>));
        Assert.False(queryResult.IsOwnType);
        Assert.Contains("[ProducesResponseType]", Assert.Single(reached, r => r.Type == typeof(FxInternalResultDto)).Path, StringComparison.Ordinal);
    }

    [Fact]
    public void Walk_SkipsIgnoredHiddenServiceAndExpandedModels()
    {
        var ids = SchemaPrefixChecks.FindReachedSchemas([typeof(FxGoodController)], typeof(FxGoodController).Assembly)
            .Select(r => r.SchemaId).ToHashSet();

        // [JsonIgnore], [ApiExplorerSettings(IgnoreApi = true)], [FromServices], and the [FromQuery]/[FromForm] models
        // themselves (their properties are walked), and core's NameIdPair.
        Assert.DoesNotContain("FxIgnoredDto", ids);
        Assert.DoesNotContain("FxHiddenDto", ids);
        Assert.DoesNotContain("FxServiceOnly", ids);
        Assert.DoesNotContain("FxSearchQuery", ids);
        Assert.DoesNotContain("FxUploadForm", ids);
        Assert.DoesNotContain("NameIdPair", ids);
    }

    [Fact]
    public void Check_PassesThePrefixedController()
    {
        Assert.Empty(SchemaPrefixChecks.Check([typeof(FxGoodController)], typeof(FxGoodController).Assembly, "Fx", SchemaPrefixChecks.ForeignSchemaIds));
    }

    [Fact]
    public void Check_FlagsANestedTypeByItsOwnName()
    {
        var problem = Assert.Single(Check(typeof(FxNestedController)));

        Assert.Contains("Schema 'Part'", problem, StringComparison.Ordinal);
        Assert.Contains("nested in FxOuterDto", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_FlagsAnEnumNamedLikeACoreSchema_TwiceOver()
    {
        var problems = Check(typeof(FxCoreNameController));

        Assert.Equal(2, problems.Count);
        Assert.Contains(problems, p => p.Contains("doesn't start with 'Fx'", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("already published by Jellyfin", StringComparison.Ordinal));
    }

    [Fact]
    public void Check_FlagsRequestBodiesAndInternalProducesResponseTypes()
    {
        var problems = Check(typeof(FxRequestController));

        Assert.Equal(2, problems.Count);
        Assert.Contains(problems, p => p.Contains("Schema 'CreateItemRequest'", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("Schema 'KitErrorDto'", StringComparison.Ordinal) && p.Contains("[ProducesResponseType]", StringComparison.Ordinal));
    }

    [Fact]
    public void Check_FlagsTwoTypesWithOneSchemaId()
    {
        var problem = Assert.Single(Check(typeof(FxTwiceController)));

        Assert.Contains("Schema id 'FxPart' belongs to several types", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_KnownUnprefixedIds_WaiveOnlyThePrefix_AndMustStayCurrent()
    {
        var foreign = new HashSet<string>(StringComparer.Ordinal) { "CreateItemRequest" };

        var problems = SchemaPrefixChecks.Check(
            [typeof(FxRequestController)],
            typeof(FxRequestController).Assembly,
            "Fx",
            foreign,
            ["CreateItemRequest", "KitErrorDto", "NoLongerThere"]);

        // The waiver doesn't cover a foreign id, and a stale entry is reported so the list only shrinks.
        Assert.Equal(2, problems.Count);
        Assert.Contains(problems, p => p.Contains("'CreateItemRequest'", StringComparison.Ordinal) && p.Contains("already published", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("'NoLongerThere' is listed", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(typeof(FxItemDto), "FxItemDto")]
    [InlineData(typeof(FxItemDto.FxItemPart), "FxItemPart")]
    [InlineData(typeof(QueryResult<FxItemDto>), "FxItemDtoQueryResult")]
    [InlineData(typeof(FxPage<QueryResult<FxItemDto>>), "FxItemDtoQueryResultFxPage")]
    [InlineData(typeof(Dictionary<string, int>), "StringInt32Dictionary")]
    public void GetSchemaId_IsSwashbuckles(Type type, string expected)
    {
        Assert.Equal(expected, SchemaPrefixChecks.GetSchemaId(type));
    }

    [Fact]
    public void ForeignList_IsTheSeededInventory()
    {
        var ids = SchemaPrefixChecks.ForeignSchemaIds;

        // 357 core ids of 10.11.5 plus 52 of the reference set of community plugins (Bookmarks 1.0's 8 left out).
        Assert.Equal(409, ids.Count);
        Assert.Contains("BaseItemDto", ids);
        Assert.Contains("SortOrder", ids);
        Assert.Contains("BookmarkItem", ids);
        Assert.Contains("AddBookmarkPayload", ids);
        Assert.DoesNotContain("BookmarkDto", ids);
        Assert.DoesNotContain("ClipDto", ids);
    }

    [Fact]
    public async Task Walk_FindsExactlyTheSchemasSwashbuckleCreates()
    {
        Type[] controllers = [typeof(FxGoodController), typeof(FxNestedController), typeof(FxCoreNameController), typeof(FxRequestController), typeof(FxTwiceController)];

        // Unique ids, so the fixtures' collisions don't stop generation; the selector records every type that became a schema.
        var created = new HashSet<Type>();
        await using var host = await MvcTestHost.StartAsync(controllers, services => services.AddSwaggerGen(options =>
        {
            ConfigureLikeJellyfin(options);
            options.CustomSchemaIds(type =>
            {
                lock (created)
                {
                    created.Add(type);
                    return "Schema" + created.Count;
                }
            });
        }));

        var document = host.Services.GetRequiredService<ISwaggerProvider>().GetSwagger("api-docs");
        var walked = SchemaPrefixChecks.FindReachedSchemas(controllers, typeof(FxGoodController).Assembly).Select(r => r.Type).ToHashSet();
        var ours = created.Where(type => InvolvesKitTests(type)).ToHashSet();

        Assert.NotEmpty(document.Components.Schemas);
        Assert.Equal(ours.Select(Name).Order(), walked.Select(Name).Order());
    }

    [Fact]
    public async Task SchemaIds_AreTheOnesSwashbuckleUses()
    {
        await using var host = await MvcTestHost.StartAsync([typeof(FxGoodController)], services => services.AddSwaggerGen(ConfigureLikeJellyfin));

        var ids = host.Services.GetRequiredService<ISwaggerProvider>().GetSwagger("api-docs").Components.Schemas.Keys.ToHashSet();

        Assert.All(ExpectedGoodIds, id => Assert.Contains(id, ids));
    }

    private static IReadOnlyList<string> Check(Type controller)
        => SchemaPrefixChecks.Check([controller], controller.Assembly, "Fx", SchemaPrefixChecks.ForeignSchemaIds);

    // The options of Jellyfin's AddJellyfinApiSwagger that change which schemas exist (v10.11.5 ApiServiceCollectionExtensions.cs).
    private static void ConfigureLikeJellyfin(Swashbuckle.AspNetCore.SwaggerGen.SwaggerGenOptions options)
    {
        options.SwaggerDoc("api-docs", new OpenApiInfo { Title = "Kit tests", Version = "1" });
        options.UseAllOfToExtendReferenceSchemas();
        options.SupportNonNullableReferenceTypes();
    }

    private static bool InvolvesKitTests(Type type)
        => type.Assembly == typeof(FxGoodController).Assembly
            || (type.IsConstructedGenericType && type.GetGenericArguments().Any(InvolvesKitTests));

    private static string Name(Type type) => type.ToString();
}
