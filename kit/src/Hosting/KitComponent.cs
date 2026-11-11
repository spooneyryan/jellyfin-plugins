using System;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kit.Hosting;

/// <summary>
/// One registration of a kit extension point that the kit creates on its own: a record owner
/// (<c>AddKitRecordOwner</c>) or a maintenance step (<c>AddKitMaintenanceStep</c>). The registration is an instance, so
/// listing the registrations never fails; each component is created through its factory in a try/catch
/// (<see cref="KitComponents.CreateAll{T}"/>), so one that can't be created costs only itself: it is logged as an Error
/// (once, until it has been created), left out, and tried again at the next use. A created component is kept.
/// </summary>
/// <remarks>
/// <see cref="IServiceProvider"/>'s <c>GetServices&lt;T&gt;()</c> resolves every registration of a type at once, so one
/// factory that throws would take every other one with it; and a <see cref="Lazy{T}"/> around it would keep that
/// exception for good.
/// </remarks>
/// <typeparam name="T">The extension point.</typeparam>
internal sealed class KitComponent<T>
    where T : class
{
    private readonly Func<IServiceProvider, T> _factory;
    private readonly object _lock = new();
    private T? _value;
    private string? _problem;

    /// <summary>
    /// Initializes a new instance of the <see cref="KitComponent{T}"/> class.
    /// </summary>
    /// <param name="name">What the logs call it (the owner's type, the step's name).</param>
    /// <param name="factory">Creates it; normally resolves a singleton the plugin registered.</param>
    public KitComponent(string name, Func<IServiceProvider, T> factory)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(factory);
        Name = name;
        _factory = factory;
    }

    /// <summary>
    /// Gets what the logs call the component.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets why the last attempt to create it failed, or null once it has been created (or before the first attempt).
    /// </summary>
    public string? Problem
    {
        get
        {
            lock (_lock)
            {
                return _problem;
            }
        }
    }

    /// <summary>
    /// Returns the component, creating it the first time (and again after a failure).
    /// </summary>
    /// <param name="provider">The service provider.</param>
    /// <returns>The component.</returns>
    /// <exception cref="Exception">Whatever the factory threw.</exception>
    public T Create(IServiceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        lock (_lock)
        {
            if (_value is not null)
            {
                return _value;
            }
        }

        // Outside the lock: the factory resolves services, which may take their own locks.
        var value = _factory(provider) ?? throw new InvalidOperationException($"The factory of {Name} returned nothing.");
        lock (_lock)
        {
            _value ??= value;
            _problem = null;
            return _value;
        }
    }

    /// <summary>
    /// Returns the component, or null when it can't be created: the first failure in a row is logged as an Error, the
    /// others at Debug, and the next call tries again.
    /// </summary>
    /// <param name="provider">The service provider.</param>
    /// <param name="logger">The logger of the kit service that asks.</param>
    /// <returns>The component, or null.</returns>
    public T? TryCreate(IServiceProvider provider, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        try
        {
            return Create(provider);
        }
        catch (Exception ex)
        {
            bool first;
            lock (_lock)
            {
                first = _problem is null;
                _problem = ex.Message;
            }

            if (first)
            {
                logger.LogError(ex, "{Component} couldn't be created, so it is left out; it is tried again at its next use", Name);
            }
            else
            {
                logger.LogDebug(ex, "{Component} still couldn't be created", Name);
            }

            return null;
        }
    }
}
