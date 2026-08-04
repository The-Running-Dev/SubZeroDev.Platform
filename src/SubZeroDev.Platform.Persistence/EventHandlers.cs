using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;

namespace SubZeroDev.Platform.Persistence;

/// <summary>One binding between a stable name, a CLR event type and the handler that dispatches it.</summary>
/// <param name="Type">The stable name a stored row's <c>type</c> column carries.</param>
/// <param name="EventType">The CLR event type.</param>
/// <param name="HandlerType">The CLR handler type.</param>
public sealed record EventHandlerRegistration(EventTypeName Type, Type EventType, Type HandlerType);

/// <summary>Collects event-handler registrations. Enforcement is at startup only, off the
/// declaration alone — both roles register the same triple, but only the dispatching role
/// constructs a handler.</summary>
public interface IEventHandlerRegistry
{
    /// <summary>Binds a stable name to a CLR event type and its handler.</summary>
    /// <typeparam name="TEvent">The event type.</typeparam>
    /// <typeparam name="THandler">The handler type.</typeparam>
    /// <param name="type">The stable name.</param>
    /// <returns>Success, or why the registration was rejected.</returns>
    Result<EventHandlerRegistrationError> Register<TEvent, THandler>(EventTypeName type)
        where TEvent : IIntegrationEvent
        where THandler : IIntegrationEventHandler<TEvent>;

    /// <summary>Resolves a registration by its stable name — the direction dispatch needs, going
    /// from a stored string to a CLR type.</summary>
    /// <param name="type">The stable name.</param>
    /// <param name="registration">The registration, when found.</param>
    /// <returns><see langword="true"/> when a registration binds <paramref name="type"/>.</returns>
    bool TryResolve(EventTypeName type, out EventHandlerRegistration registration);

    /// <summary>Resolves a registration by its CLR event type — the direction enqueue needs, going
    /// from <c>TEvent</c> to the name to stamp.</summary>
    /// <param name="eventType">The CLR event type.</param>
    /// <param name="registration">The registration, when found.</param>
    /// <returns><see langword="true"/> when a registration binds <paramref name="eventType"/>.</returns>
    bool TryResolve(Type eventType, out EventHandlerRegistration registration);

    /// <summary>Every registration, in registration order.</summary>
    IReadOnlyList<EventHandlerRegistration> Registered { get; }

    /// <summary>Closes the registry. One-way: registration after this returns a failure.</summary>
    void Freeze();
}

/// <inheritdoc cref="IEventHandlerRegistry"/>
internal sealed class EventHandlerRegistry : IEventHandlerRegistry
{
    private readonly List<EventHandlerRegistration> _registered = [];
    private readonly Dictionary<EventTypeName, EventHandlerRegistration> _byName = [];
    private readonly Dictionary<Type, EventHandlerRegistration> _byEventType = [];
    private readonly Lock _gate = new();
    private bool _frozen;

    public IReadOnlyList<EventHandlerRegistration> Registered => _registered;

    public Result<EventHandlerRegistrationError> Register<TEvent, THandler>(EventTypeName type)
        where TEvent : IIntegrationEvent
        where THandler : IIntegrationEventHandler<TEvent>
    {
        lock (_gate)
        {
            if (_frozen)
            {
                return Result<EventHandlerRegistrationError>.Failure(
                    EventHandlerRegistrationError.RegistryFrozen(type));
            }

            if (_byName.TryGetValue(type, out var existingByName))
            {
                return Result<EventHandlerRegistrationError>.Failure(
                    EventHandlerRegistrationError.DuplicateHandlerForType(type, existingByName.HandlerType, typeof(THandler)));
            }

            if (_byEventType.TryGetValue(typeof(TEvent), out var existingByEvent))
            {
                return Result<EventHandlerRegistrationError>.Failure(
                    EventHandlerRegistrationError.DuplicateNameForEventType(typeof(TEvent), existingByEvent.Type, type));
            }

            var registration = new EventHandlerRegistration(type, typeof(TEvent), typeof(THandler));
            _registered.Add(registration);
            _byName[type] = registration;
            _byEventType[typeof(TEvent)] = registration;
            return Result<EventHandlerRegistrationError>.Success();
        }
    }

    public bool TryResolve(EventTypeName type, out EventHandlerRegistration registration) =>
        _byName.TryGetValue(type, out registration!);

    public bool TryResolve(Type eventType, out EventHandlerRegistration registration) =>
        _byEventType.TryGetValue(eventType, out registration!);

    public void Freeze()
    {
        lock (_gate)
        {
            _frozen = true;
        }
    }
}

/// <summary>Queues a compile-time-typed registration for <see cref="EventHandlerRegistryStartup"/> to
/// apply once the container exists. <see cref="IEventHandlerRegistry.Register{TEvent, THandler}"/> is
/// generic and modules only hold an <see cref="IServiceCollection"/> at compose time, before any
/// registry instance exists — this closure is what lets a module's call site stay one line while the
/// actual <c>Register</c> call happens later, against the real registry.</summary>
internal interface IEventHandlerRegistrant
{
    Type HandlerType { get; }

    Result<EventHandlerRegistrationError> Apply(IEventHandlerRegistry registry);
}

internal sealed class EventHandlerRegistrant<TEvent, THandler>(EventTypeName type) : IEventHandlerRegistrant
    where TEvent : IIntegrationEvent
    where THandler : IIntegrationEventHandler<TEvent>
{
    public Type HandlerType { get; } = typeof(THandler);

    public Result<EventHandlerRegistrationError> Apply(IEventHandlerRegistry registry) =>
        registry.Register<TEvent, THandler>(type);
}

/// <summary>Registers an event and its handler from a module's <c>Register(IServiceCollection)</c>,
/// the same compose-time call site a module already uses for its migrations. The web host must call
/// this too, in order to enqueue — it never constructs the handler, but the registration is a
/// statement both roles make identically.</summary>
public static class PlatformEventHandlerExtensions
{
    /// <summary>Registers <typeparamref name="THandler"/> for dependency injection and queues the
    /// name-to-type binding for startup.</summary>
    /// <typeparam name="TEvent">The event type.</typeparam>
    /// <typeparam name="THandler">The handler type.</typeparam>
    /// <param name="services">The host's service collection.</param>
    /// <param name="type">The event's stable name.</param>
    /// <returns>The same collection, so calls chain.</returns>
    public static IServiceCollection AddPlatformEventHandler<TEvent, THandler>(
        this IServiceCollection services, EventTypeName type)
        where TEvent : IIntegrationEvent
        where THandler : class, IIntegrationEventHandler<TEvent>
    {
        ArgumentNullException.ThrowIfNull(services);

        // Registered in both roles identically. Only the worker's EventHandlerRegistryStartup ever
        // resolves it — the web host's container holds the registration and never constructs it.
        services.TryAddScoped<THandler>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IEventHandlerRegistrant>(new EventHandlerRegistrant<TEvent, THandler>(type)));

        return services;
    }
}

/// <summary>Applies every queued registration to the real registry and freezes it, then — in the
/// worker role only — attempts to construct each registered handler, so a missing dependency aborts
/// startup with a named error rather than surfacing the first time a message dispatches.</summary>
internal sealed class EventHandlerRegistryStartup(
    IEnumerable<IEventHandlerRegistrant> registrants,
    IEventHandlerRegistry registry,
    PlatformOptions options,
    IServiceProvider serviceProvider) : IHostedLifecycleService
{
    public Task StartingAsync(CancellationToken cancellationToken)
    {
        foreach (var registrant in registrants)
        {
            var applied = registrant.Apply(registry);
            if (!applied.IsSuccess)
            {
                throw new PersistenceStartupException(applied.Error);
            }
        }

        registry.Freeze();

        if (options.Role == HostRole.Worker)
        {
            using var scope = serviceProvider.CreateScope();
            foreach (var registration in registry.Registered)
            {
                try
                {
                    scope.ServiceProvider.GetRequiredService(registration.HandlerType);
                }
                catch (Exception exception) when (exception is not PersistenceStartupException)
                {
                    throw new PersistenceStartupException(
                        EventHandlerRegistrationError.HandlerNotConstructible(registration.HandlerType, exception.Message));
                }
            }
        }

        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
