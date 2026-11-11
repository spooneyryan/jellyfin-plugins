using System;
using System.Collections.Concurrent;
using System.Linq;
using Jellyfin.Plugin.Kit.Hosting;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace Jellyfin.Plugin.Kit.Tests.Hosting;

/// <summary>
/// KitStartupLog writes through Serilog's static logger, as Jellyfin's own log lines do (Serilog 4.2.0, the version
/// Jellyfin 10.11.5 ships). The sandbox check of the ABI guard shows the same line in a real server log.
/// </summary>
[Collection(SerilogCollection.Name)]
public sealed class KitStartupLogTests
{
    [Fact]
    public void Error_WritesAnErrorWithTheCategoryAsSourceContext()
    {
        var sink = new CollectingSink();
        var category = "Jellyfin.Plugin.KitTest.Kit.AbiGuard." + Guid.NewGuid().ToString("N");
        var previous = Log.Logger;
        Log.Logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();
        try
        {
            Assert.True(KitStartupLog.Error(category, "Built for Jellyfin {10.11}: the plugin is inactive."));
        }
        finally
        {
            Log.Logger = previous;
        }

        var entry = Assert.Single(sink.Events, e => e.Properties.TryGetValue("SourceContext", out var value) && value.ToString() == $"\"{category}\"");
        Assert.Equal(LogEventLevel.Error, entry.Level);

        // Braces are escaped, so the message is never parsed as a template.
        Assert.Equal("Built for Jellyfin {10.11}: the plugin is inactive.", entry.RenderMessage(null));
    }

    [Fact]
    public void Error_WithSilentSerilog_DoesNotThrow()
    {
        // Serilog's default static logger discards everything; touching it loads Serilog into the default context.
        Assert.NotNull(Log.Logger);
        Assert.True(KitStartupLog.Error("Jellyfin.Plugin.KitTest.Kit.AbiGuard", "Nothing listens."));
    }

    private sealed class CollectingSink : ILogEventSink
    {
        public ConcurrentQueue<LogEvent> Events { get; } = new();

        public void Emit(LogEvent logEvent) => Events.Enqueue(logEvent);
    }
}

/// <summary>Tests that replace Serilog's static logger don't run in parallel with each other.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SerilogCollection
{
    public const string Name = "Serilog static logger";
}
