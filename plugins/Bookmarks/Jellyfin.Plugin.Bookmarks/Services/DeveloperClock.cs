using System;
using Jellyfin.Plugin.Bookmarks.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Bookmarks.Services;

/// <summary>
/// The clock of the kit's record lifecycle in Bookmarks (<c>KitOptions.Clock</c>): the system clock, moved on
/// by the setting <c>DeveloperClockOffsetDays</c> in a Debug build only, so a sandbox can check that detached records
/// move to the archive after <c>DetachedRetentionDays</c> without waiting days. A Release build always runs on the
/// system clock.
/// </summary>
internal sealed class DeveloperClock : TimeProvider
{
    /// <summary>The largest offset honoured, in days.</summary>
    public const int MaxOffsetDays = 3650;

    private readonly Func<int> _offsetDays;

    /// <summary>
    /// Initializes a new instance of the <see cref="DeveloperClock"/> class.
    /// </summary>
    /// <param name="offsetDays">Reads the offset in days, each time the time is asked for.</param>
    public DeveloperClock(Func<int> offsetDays)
    {
        ArgumentNullException.ThrowIfNull(offsetDays);
        _offsetDays = offsetDays;
    }

    /// <summary>
    /// Gets a value indicating whether this build honours the offset (a Debug build).
    /// </summary>
    public static bool IsHonoured =>
#if DEBUG
        true;
#else
        false;
#endif

    /// <summary>
    /// Creates the lifecycle's clock for the plugin's services: a <see cref="DeveloperClock"/> over the setting in a Debug
    /// build, the system clock otherwise.
    /// </summary>
    /// <param name="services">The server's services.</param>
    /// <returns>The clock.</returns>
    public static TimeProvider For(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (!IsHonoured)
        {
            return TimeProvider.System;
        }

        var configuration = services.GetRequiredService<IPluginConfigurationProvider>();
        return new DeveloperClock(() => configuration.GetConfiguration().DeveloperClockOffsetDays);
    }

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + TimeSpan.FromDays(Math.Clamp(_offsetDays(), 0, MaxOffsetDays));
}
