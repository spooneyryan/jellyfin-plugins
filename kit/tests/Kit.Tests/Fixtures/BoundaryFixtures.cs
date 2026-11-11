using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Diagnostics;
using Jellyfin.Plugin.Kit.Web.Pages;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

// Fixture "plugins" for the public/internal boundary check (kit/testsupport/Api/PublicBoundaryChecks.cs). Each
// namespace is checked on its own as a plugin's root namespace: Good follows every rule with the prefix "Fxb", and each
// Bad.* namespace breaks one. The kit's own types (IKitDiagnostics, IKitPages, CurrentUser) are the real ones, compiled
// into this assembly as into a plugin.
#pragma warning disable SA1402, SA1649, CA1812 // Several small fixture types per file; some are never instantiated.

namespace Jellyfin.Plugin.Kit.Tests.Boundary.Good
{
    /// <summary>Reached by the actions: a DTO, outside Api.Models as enums may be (like Bookmarks' Model/BookmarkClipStatus).</summary>
    public enum FxbState
    {
        Draft,
        Done
    }

    /// <summary>Not reached by any action: a DTO by its prefix (like Bookmarks' BookmarkOrigin).</summary>
    public enum FxbOrigin
    {
        Native,
        Imported
    }
}

// The DTO classes: reached from the actions, and in the plugin's Api.Models namespace.
namespace Jellyfin.Plugin.Kit.Tests.Boundary.Good.Api.Models
{
    public sealed class FxbItemDto
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public FxbState State { get; set; }

        public IReadOnlyList<FxbTagDto> Tags { get; set; } = [];
    }

    public sealed class FxbTagDto
    {
        public string Text { get; set; } = string.Empty;
    }

    public sealed class FxbCreateRequest
    {
        public string Name { get; set; } = string.Empty;
    }

    /// <summary>A [FromQuery] model: Swashbuckle documents its properties, not the type, and it is still a DTO.</summary>
    public sealed class FxbSearchQuery
    {
        public string? Term { get; set; }
    }
}

namespace Jellyfin.Plugin.Kit.Tests.Boundary.Good
{
    using Jellyfin.Plugin.Kit.Tests.Boundary.Good.Api.Models;

    /// <summary>The service the controller takes: DTOs, Jellyfin's User and BCL types only.</summary>
    public interface IFxbItems
    {
        FxbOrigin DefaultOrigin { get; }

        IReadOnlyList<FxbItemDto> List(User user, FxbSearchQuery query);

        Task<FxbItemDto> CreateAsync(User user, FxbCreateRequest request, CancellationToken cancellationToken);
    }

    internal sealed record FxbRecord(Guid Id, string Name, FxbState State);

    internal sealed class FxbStore
    {
        public List<FxbRecord> Records { get; } = [];
    }

    internal static class FxbMapper
    {
        public static FxbItemDto ToDto(FxbRecord record) => new() { Id = record.Id, Name = record.Name, State = record.State };
    }

    /// <summary>The implementation maps records to DTOs itself.</summary>
    internal sealed class FxbItems : IFxbItems
    {
        private readonly FxbStore _store;

        public FxbItems(FxbStore store)
        {
            _store = store;
        }

        public FxbOrigin DefaultOrigin => FxbOrigin.Native;

        public IReadOnlyList<FxbItemDto> List(User user, FxbSearchQuery query)
            => _store.Records.Where(r => query.Term is null || r.Name.Contains(query.Term, StringComparison.Ordinal)).Select(FxbMapper.ToDto).ToList();

        public Task<FxbItemDto> CreateAsync(User user, FxbCreateRequest request, CancellationToken cancellationToken)
        {
            var record = new FxbRecord(Guid.NewGuid(), request.Name, FxbState.Draft);
            _store.Records.Add(record);
            return Task.FromResult(FxbMapper.ToDto(record));
        }
    }

    /// <summary>Async actions, a closure and an anonymous route value, all of them only on DTOs; the kit's helpers.</summary>
    [ApiController]
    [Authorize]
    [Route("Fxb")]
    public class FxbController : ControllerBase
    {
        private readonly IFxbItems _items;
        private readonly IUserManager _users;
        private readonly IKitDiagnostics _diagnostics;

        public FxbController(IFxbItems items, IUserManager users, IKitDiagnostics diagnostics)
        {
            _items = items;
            _users = users;
            _diagnostics = diagnostics;
        }

        [HttpGet]
        public ActionResult<IReadOnlyList<FxbItemDto>> List([FromQuery] FxbSearchQuery query) => Ok(_items.List(CurrentCaller(), query));

        [HttpPost]
        public async Task<ActionResult<FxbItemDto>> Create([FromBody] FxbCreateRequest request, CancellationToken cancellationToken)
        {
            var item = await _items.CreateAsync(CurrentCaller(), request, cancellationToken).ConfigureAwait(false);
            return CreatedAtAction(nameof(List), new { id = item.Id }, item);
        }

        [HttpGet("Names")]
        public ActionResult<IEnumerable<string>> Names([FromQuery] FxbState state)
            => Ok(_items.List(CurrentCaller(), new FxbSearchQuery()).Where(item => item.State == state).Select(item => item.Name).ToList());

        [HttpGet("Diagnostics")]
        public ActionResult<IDictionary<string, object?>> Diagnostics() => Ok(_diagnostics.Describe());

        private User CurrentCaller() => User.RequireUser(_users);
    }

    /// <summary>A part Jellyfin creates: it takes the service provider and resolves internal services lazily.</summary>
    public sealed class FxbTask : IScheduledTask
    {
        private readonly IServiceProvider _services;

        public FxbTask(IServiceProvider services)
        {
            _services = services;
        }

        public string Name => "Fxb";

        public string Key => "Fxb";

        public string Description => "Fxb";

        public string Category => "Fxb";

        public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            _services.GetRequiredService<FxbStore>().Records.Clear();
            return Task.CompletedTask;
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];
    }

    public sealed class FxbRegistrator : IPluginServiceRegistrator
    {
        public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
        {
            serviceCollection.AddSingleton<FxbStore>();
            serviceCollection.AddSingleton<IFxbItems, FxbItems>();
        }
    }

    public sealed class FxbConfiguration : BasePluginConfiguration
    {
        public FxbOrigin DefaultOrigin { get; set; }

        public bool Enabled { get; set; } = true;
    }

    public class FxbPlugin : BasePlugin<FxbConfiguration>
    {
        public FxbPlugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
        }

        public static FxbPlugin? Instance { get; private set; }

        public override string Name => "Fxb";

        public override Guid Id => Guid.Empty;
    }
}

namespace Jellyfin.Plugin.Kit.Tests.Boundary.Good.Integration
{
    /// <summary>An Integration facade: BCL types only.</summary>
    public static class FxbSummariesV1
    {
        public static IReadOnlyDictionary<Guid, (double? AverageStars, int Count)> GetSummaries(IReadOnlyList<Guid> itemIds)
            => itemIds.ToDictionary(id => id, _ => ((double?)null, 0));

        public static void OnItemsMoved(string movesJson) => ArgumentNullException.ThrowIfNull(movesJson);
    }
}

namespace Jellyfin.Plugin.Kit.Tests.Boundary.Bad.PublicRecord
{
    /// <summary>A record left public, returned by the service interface (the Bookmarks 1.0 shape the boundary removes).</summary>
    public sealed record FxbRecord(Guid Id, string Name);

    public interface IFxbItems
    {
        FxbRecord Get(Guid id);
    }

    [ApiController]
    [Authorize]
    [Route("Fxb/PublicRecord")]
    public sealed class FxbPublicRecordController : ControllerBase
    {
        private readonly IFxbItems _items;

        public FxbPublicRecordController(IFxbItems items)
        {
            _items = items;
        }

        [HttpGet("{id}")]
        public IActionResult Get([FromRoute] Guid id) => Ok(_items.Get(id));
    }
}

namespace Jellyfin.Plugin.Kit.Tests.Boundary.Bad.ReachedRecord.Model
{
    /// <summary>
    /// A record left public with the prefix (like Bookmarks' <c>Bookmark</c>), returned by an action as well as by the
    /// service: Swashbuckle reaches it, yet it is no DTO.
    /// </summary>
    public sealed record FxbRecord(Guid Id, string Name);
}

namespace Jellyfin.Plugin.Kit.Tests.Boundary.Bad.ReachedRecord.Storage
{
    /// <summary>A store left public, returned by an action too.</summary>
    public sealed class FxbStore
    {
        public List<Model.FxbRecord> Records { get; } = [];
    }
}

namespace Jellyfin.Plugin.Kit.Tests.Boundary.Bad.ReachedRecord
{
    public interface IFxbItems
    {
        Storage.FxbStore Store { get; }

        Model.FxbRecord Get(Guid id);
    }

    [ApiController]
    [Authorize]
    [Route("Fxb/ReachedRecord")]
    public sealed class FxbReachedRecordController : ControllerBase
    {
        private readonly IFxbItems _items;

        public FxbReachedRecordController(IFxbItems items)
        {
            _items = items;
        }

        [HttpGet("{id}")]
        public ActionResult<Model.FxbRecord> Get([FromRoute] Guid id) => _items.Get(id);

        [HttpGet("Export")]
        public ActionResult<Storage.FxbStore> Export() => _items.Store;
    }
}

namespace Jellyfin.Plugin.Kit.Tests.Boundary.Bad.KitInService
{
    public interface IFxbItems
    {
        IKitPages Pages();
    }

    [ApiController]
    [Authorize]
    [Route("Fxb/KitInService")]
    public sealed class FxbKitInServiceController : ControllerBase
    {
        private readonly IFxbItems _items;

        public FxbKitInServiceController(IFxbItems items)
        {
            _items = items;
        }

        [HttpGet("{key}")]
        public IActionResult Get([FromRoute] string key) => _items.Pages().Shell(key);
    }
}

namespace Jellyfin.Plugin.Kit.Tests.Boundary.Bad.ControllerReadsStore.Api.Models
{
    public sealed class FxbItemDto
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }
}

namespace Jellyfin.Plugin.Kit.Tests.Boundary.Bad.ControllerReadsStore
{
    using Jellyfin.Plugin.Kit.Tests.Boundary.Bad.ControllerReadsStore.Api.Models;

    internal sealed record FxbRecord(Guid Id, string Name);

    internal sealed class FxbStore
    {
        public List<FxbRecord> Records { get; } = [];
    }

    internal static class FxbMapper
    {
        public static FxbItemDto ToDto(FxbRecord record) => new() { Id = record.Id, Name = record.Name };
    }

    /// <summary>
    /// Its signatures are all public, but its code reads the internal store through the request's services and maps the
    /// records itself, in an async action with a closure.
    /// </summary>
    [ApiController]
    [Authorize]
    [Route("Fxb/ReadsStore")]
    public sealed class FxbReadsStoreController : ControllerBase
    {
        [HttpGet("{id}")]
        public async Task<ActionResult<FxbItemDto>> Get([FromRoute] Guid id, CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            var store = HttpContext.RequestServices.GetRequiredService<FxbStore>();
            return store.Records.Where(record => record.Id == id).Select(FxbMapper.ToDto).First();
        }
    }
}

namespace Jellyfin.Plugin.Kit.Tests.Boundary.Bad.ConcreteInConstructor.Api.Models
{
    public sealed class FxbItemDto
    {
        public Guid Id { get; set; }
    }
}

namespace Jellyfin.Plugin.Kit.Tests.Boundary.Bad.ConcreteInConstructor
{
    using Jellyfin.Plugin.Kit.Tests.Boundary.Bad.ConcreteInConstructor.Api.Models;

    /// <summary>A public implementation taken by the controller instead of an interface.</summary>
    public sealed class FxbItems
    {
        public FxbItemDto Get(Guid id) => new() { Id = id };
    }

    [ApiController]
    [Authorize]
    [Route("Fxb/Concrete")]
    public sealed class FxbConcreteController : ControllerBase
    {
        private readonly FxbItems _items;

        public FxbConcreteController(FxbItems items)
        {
            _items = items;
        }

        [HttpGet("{id}")]
        public ActionResult<FxbItemDto> Get([FromRoute] Guid id) => _items.Get(id);
    }
}

namespace Jellyfin.Plugin.Kit.Tests.Boundary.Bad.DtoHoldsService.Api.Models
{
    public sealed class FxbItemDto
    {
        public Guid Id { get; set; }

        public IFxbItems? Items { get; set; }
    }
}

namespace Jellyfin.Plugin.Kit.Tests.Boundary.Bad.DtoHoldsService
{
    using Jellyfin.Plugin.Kit.Tests.Boundary.Bad.DtoHoldsService.Api.Models;

    public interface IFxbItems
    {
        FxbItemDto Get(Guid id);
    }

    [ApiController]
    [Authorize]
    [Route("Fxb/DtoHoldsService")]
    public sealed class FxbDtoHoldsServiceController : ControllerBase
    {
        private readonly IFxbItems _items;

        public FxbDtoHoldsServiceController(IFxbItems items)
        {
            _items = items;
        }

        [HttpGet("{id}")]
        public ActionResult<FxbItemDto> Get([FromRoute] Guid id) => _items.Get(id);
    }
}

namespace Jellyfin.Plugin.Kit.Tests.Boundary.Bad.Unused
{
    /// <summary>No public constructor takes it.</summary>
    public interface IFxbHelper
    {
        int Count();
    }

    /// <summary>A helper that needn't be public.</summary>
    public static class FxbHelpers
    {
        public static int Twice(int value) => value * 2;
    }
}

namespace Jellyfin.Plugin.Kit.Tests.Boundary.Bad.Facade.Api.Models
{
    public sealed class FxbItemDto
    {
        public Guid Id { get; set; }
    }
}

namespace Jellyfin.Plugin.Kit.Tests.Boundary.Bad.Facade
{
    using Jellyfin.Plugin.Kit.Tests.Boundary.Bad.Facade.Api.Models;

    [ApiController]
    [Authorize]
    [Route("Fxb/Facade")]
    public sealed class FxbFacadeController : ControllerBase
    {
        [HttpGet]
        public ActionResult<FxbItemDto> Get() => new FxbItemDto();
    }
}

namespace Jellyfin.Plugin.Kit.Tests.Boundary.Bad.Facade.Integration
{
    using Jellyfin.Plugin.Kit.Tests.Boundary.Bad.Facade.Api.Models;

    /// <summary>A facade with a Jellyfin type and a DTO in its signature, which other plugins' load contexts can't share.</summary>
    public static class FxbLookupV1
    {
        public static FxbItemDto Find(User user, Guid itemId) => new() { Id = itemId == Guid.Empty ? user.Id : itemId };
    }
}

namespace Jellyfin.Plugin.Kit.Tests.Boundary.Bad.ProtectedNested
{
    /// <summary>
    /// The compiler lets an unsealed public class expose its protected nested type through a protected member, but a
    /// derived class in another assembly then sees a type that isn't public.
    /// </summary>
    [ApiController]
    [Authorize]
    [Route("Fxb/Protected")]
    [SuppressMessage("Design", "CA1034:Nested types should not be visible", Justification = "Fixture.")]
    public class FxbProtectedController : ControllerBase
    {
        [HttpGet]
        public IActionResult Get() => Ok(MakePart().Index);

        protected static FxbPart MakePart() => new();

        protected internal sealed class FxbPart
        {
            public int Index { get; set; }
        }
    }
}
