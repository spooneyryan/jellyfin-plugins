using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kit.TestSupport.Logging;

/// <summary>
/// A logger that keeps every entry, for tests that check what was logged.
/// </summary>
public class ListLogger : ILogger
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    public IReadOnlyList<LogEntry> Entries => _entries.ToList();

    public IEnumerable<LogEntry> At(LogLevel level) => _entries.Where(e => e.Level == level);

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
        => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        _entries.Enqueue(new LogEntry(logLevel, formatter(state, exception), exception));
    }
}

/// <summary>
/// A <see cref="ListLogger"/> for a service that takes an <see cref="ILogger{TCategoryName}"/>.
/// </summary>
/// <typeparam name="TCategory">The logger's category type.</typeparam>
public sealed class ListLogger<TCategory> : ListLogger, ILogger<TCategory>
{
}

/// <summary>
/// One entry of a <see cref="ListLogger"/>.
/// </summary>
/// <param name="Level">The level.</param>
/// <param name="Message">The formatted message.</param>
/// <param name="Exception">The exception, if any.</param>
public sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);
