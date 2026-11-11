using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

// Fixture types for the schema-prefix walker (kit/testsupport/Api/SchemaPrefixChecks.cs). The "good" controller uses
// the prefix "Fx" throughout and covers every shape a schema can be reached by; the "bad" controllers each break the rule
// one way. The Swashbuckle test runs the same controllers through Swashbuckle 6.2.3 and compares the schemas it creates
// with the walk.
#pragma warning disable SA1402, SA1649 // Several small fixture types per file.

namespace Jellyfin.Plugin.Kit.Tests.Fixtures
{
    public enum FxItemStatus
    {
        Draft,
        Ready
    }

    public enum FxSortMode
    {
        Name,
        Date
    }

    public enum FxUploadKind
    {
        Poster,
        Clip
    }

    /// <summary>Every shape a schema can be reached by, all prefixed.</summary>
    [ApiController]
    [Authorize]
    [Route("Fx")]
    public sealed class FxGoodController : ControllerBase
    {
        [HttpGet("Items")]
        public ActionResult<QueryResult<FxItemDto>> GetItems() => new QueryResult<FxItemDto>();

        [HttpGet("Items/{itemId}")]
        public async Task<ActionResult<FxItemDto>> GetItem([FromRoute] Guid itemId, CancellationToken cancellationToken)
        {
            await Task.Delay(0, cancellationToken).ConfigureAwait(false);
            return new FxItemDto { Id = itemId };
        }

        [HttpPost("Items")]
        [ProducesResponseType(typeof(FxInternalResultDto), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public IActionResult PostItem([FromBody] FxCreateItemRequest request) => Ok(request);

        [HttpGet("Search")]
        public ActionResult<IReadOnlyList<FxItemDto>> Search([FromQuery] FxSearchQuery query) => new List<FxItemDto>();

        [HttpPost("Upload")]
        public ActionResult<string> Upload([FromForm] FxUploadForm form) => form?.File?.FileName ?? string.Empty;

        [HttpGet("Page")]
        public ActionResult<FxPage<FxItemDto>> GetPage() => new FxPage<FxItemDto>();

        [HttpGet("Hidden")]
        [ApiExplorerSettings(IgnoreApi = true)]
        public ActionResult<FxHiddenDto> Hidden() => new FxHiddenDto();

        [HttpGet("Name")]
        public ActionResult<NameIdPair> GetNameIdPair([FromServices] FxServiceOnly service) => new NameIdPair();
    }

    public sealed class FxItemDto
    {
        public Guid Id { get; set; }

        public FxItemStatus Status { get; set; }

        public FxItemPart? FirstPart { get; set; }

        public IReadOnlyList<FxItemTagDto> Tags { get; set; } = [];

        public Dictionary<string, FxValueDto> Values { get; set; } = [];

        [JsonIgnore]
        public FxIgnoredDto? Ignored { get; set; }

        public NameIdPair? Core { get; set; }

        /// <summary>Nested, and prefixed itself: its schema id is its own name.</summary>
        public sealed class FxItemPart
        {
            public int Index { get; set; }
        }
    }

    public sealed class FxItemTagDto
    {
        public string Name { get; set; } = string.Empty;
    }

    public sealed class FxValueDto
    {
        public int Value { get; set; }
    }

    public sealed class FxIgnoredDto
    {
        public int Value { get; set; }
    }

    public sealed class FxHiddenDto
    {
        public int Value { get; set; }
    }

    public sealed class FxServiceOnly
    {
        public int Value { get; set; }
    }

    public sealed class FxCreateItemRequest
    {
        public string Name { get; set; } = string.Empty;

        public FxItemStatus? Status { get; set; }
    }

    public sealed class FxSearchQuery
    {
        public string? Text { get; set; }

        public FxSortMode SortBy { get; set; }
    }

    public sealed class FxUploadForm
    {
        public IFormFile? File { get; set; }

        public FxUploadKind Kind { get; set; }
    }

    public sealed class FxPage<T>
    {
        public IReadOnlyList<T> Items { get; set; } = [];

        public int Total { get; set; }
    }

    /// <summary>Internal, reached only through [ProducesResponseType].</summary>
    internal sealed class FxInternalResultDto
    {
        public string Name { get; set; } = string.Empty;
    }
}

namespace Jellyfin.Plugin.Kit.Tests.Fixtures.Bad
{
    /// <summary>A prefixed outer type with an unprefixed nested type.</summary>
    [ApiController]
    [Authorize]
    [Route("Fx/Nested")]
    public sealed class FxNestedController : ControllerBase
    {
        [HttpGet]
        public ActionResult<FxOuterDto> Get() => new FxOuterDto();
    }

    public sealed class FxOuterDto
    {
        public Part? First { get; set; }

        public sealed class Part
        {
            public int Index { get; set; }
        }
    }

    /// <summary>A plugin enum named like core's SortOrder.</summary>
    [ApiController]
    [Authorize]
    [Route("Fx/Core")]
    public sealed class FxCoreNameController : ControllerBase
    {
        [HttpGet]
        public ActionResult<FxSortedDto> Get() => new FxSortedDto();
    }

    public enum SortOrder
    {
        Up,
        Down
    }

    public sealed class FxSortedDto
    {
        public SortOrder Order { get; set; }
    }

    /// <summary>An unprefixed request body and an unprefixed internal response type.</summary>
    [ApiController]
    [Authorize]
    [Route("Fx/Request")]
    public sealed class FxRequestController : ControllerBase
    {
        [HttpPost]
        [ProducesResponseType(typeof(KitErrorDto), StatusCodes.Status200OK)]
        public IActionResult Post([FromBody] CreateItemRequest request) => Ok(request);
    }

    public sealed class CreateItemRequest
    {
        public string Name { get; set; } = string.Empty;
    }

    internal sealed class KitErrorDto
    {
        public string Message { get; set; } = string.Empty;
    }

    /// <summary>Two prefixed outer types, each with a nested type of the same name: one schema id for two types.</summary>
    [ApiController]
    [Authorize]
    [Route("Fx/Twice")]
    public sealed class FxTwiceController : ControllerBase
    {
        [HttpGet("A")]
        public ActionResult<FxFirstDto> GetA() => new FxFirstDto();

        [HttpGet("B")]
        public ActionResult<FxSecondDto> GetB() => new FxSecondDto();
    }

    public sealed class FxFirstDto
    {
        public FxPart? Part { get; set; }

        public sealed class FxPart
        {
            public int A { get; set; }
        }
    }

    public sealed class FxSecondDto
    {
        public FxPart? Part { get; set; }

        public sealed class FxPart
        {
            public int B { get; set; }
        }
    }
}
