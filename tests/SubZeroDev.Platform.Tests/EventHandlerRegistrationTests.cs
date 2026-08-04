using Microsoft.Extensions.DependencyInjection;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Persistence;
using SubZeroDev.Platform.Testing;

namespace SubZeroDev.Platform.Tests;

/// <summary>Startup enforcement over <see cref="IEventHandlerRegistry"/>: name and handler
/// uniqueness check identically in both roles off the declaration alone, and only the dispatching
/// role ever constructs a handler.</summary>
public sealed class EventHandlerRegistrationTests
{
    [Fact]
    public async Task A_second_handler_for_an_already_registered_name_aborts_startup_naming_both()
    {
        var thrown = await Assert.ThrowsAsync<PersistenceStartupException>(() =>
            PlatformTestHost.CreateBuilder()
                .WithProvider(PersistenceProvider.Sqlite)
                .WithServices(services =>
                {
                    services.AddPlatformEventHandler<TestEvent, TestEventHandler>(new EventTypeName("shared.name"));
                    services.AddPlatformEventHandler<OtherTestEvent, OtherTestEventHandler>(new EventTypeName("shared.name"));
                })
                .StartAsync(CancellationToken.None));

        Assert.Equal(nameof(EventHandlerRegistrationError.DuplicateHandlerForType), thrown.Error.Code);
        var detail = Assert.IsType<EventHandlerRegistrationError>(thrown.Error).Detail;
        Assert.Contains(nameof(TestEventHandler), detail, StringComparison.Ordinal);
        Assert.Contains(nameof(OtherTestEventHandler), detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_second_name_for_an_already_bound_clr_type_aborts_startup()
    {
        var thrown = await Assert.ThrowsAsync<PersistenceStartupException>(() =>
            PlatformTestHost.CreateBuilder()
                .WithProvider(PersistenceProvider.Sqlite)
                .WithServices(services =>
                {
                    services.AddPlatformEventHandler<TestEvent, TestEventHandler>(new EventTypeName("name.a"));
                    services.AddPlatformEventHandler<TestEvent, SecondTestEventHandler>(new EventTypeName("name.b"));
                })
                .StartAsync(CancellationToken.None));

        Assert.Equal(nameof(EventHandlerRegistrationError.DuplicateNameForEventType), thrown.Error.Code);
    }

    [Fact]
    public async Task A_handler_whose_dependency_cannot_be_resolved_aborts_worker_startup_only()
    {
        var thrown = await Assert.ThrowsAsync<PersistenceStartupException>(() =>
            PlatformTestHost.CreateBuilder()
                .WithRole(HostRole.Worker)
                .WithProvider(PersistenceProvider.Sqlite)
                .WithServices(services =>
                    services.AddPlatformEventHandler<TestEvent, UnconstructibleTestEventHandler>(new EventTypeName("test.event")))
                .StartAsync(CancellationToken.None));

        Assert.Equal(nameof(EventHandlerRegistrationError.HandlerNotConstructible), thrown.Error.Code);
        var detail = Assert.IsType<EventHandlerRegistrationError>(thrown.Error).Detail;
        Assert.Contains(nameof(UnconstructibleTestEventHandler), detail, StringComparison.Ordinal);

        // The web role records the same triple in order to enqueue, but its container never
        // constructs the handler — so the identical registration must not fail it.
        await using var webHost = await PlatformTestHost.CreateBuilder()
            .WithRole(HostRole.Web)
            .WithProvider(PersistenceProvider.Sqlite)
            .WithServices(services =>
                services.AddPlatformEventHandler<TestEvent, UnconstructibleTestEventHandler>(new EventTypeName("test.event")))
            .StartAsync(CancellationToken.None);

        var registry = webHost.Services.GetRequiredService<IEventHandlerRegistry>();
        Assert.True(registry.TryResolve(new EventTypeName("test.event"), out _));
    }

    [Fact]
    public async Task Registration_resolves_by_name_and_by_clr_type_after_startup()
    {
        await using var host = await PlatformTestHost.CreateBuilder()
            .WithProvider(PersistenceProvider.Sqlite)
            .WithServices(services =>
                services.AddPlatformEventHandler<TestEvent, TestEventHandler>(new EventTypeName("test.event")))
            .StartAsync(CancellationToken.None);

        var registry = host.Services.GetRequiredService<IEventHandlerRegistry>();

        Assert.True(registry.TryResolve(new EventTypeName("test.event"), out var byName));
        Assert.Equal(typeof(TestEvent), byName.EventType);
        Assert.Equal(typeof(TestEventHandler), byName.HandlerType);

        Assert.True(registry.TryResolve(typeof(TestEvent), out var byType));
        Assert.Equal(new EventTypeName("test.event"), byType.Type);

        Assert.Single(registry.Registered);
    }
}
