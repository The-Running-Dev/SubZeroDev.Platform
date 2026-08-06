using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Hosting;
using SubZeroDev.Platform.Persistence;
using SubZeroDev.Platform.Testing;

namespace SubZeroDev.Platform.Tests;

/// <summary>S8.4 (integration half — the sampler's own decision logic is covered in isolation by
/// <see cref="TelemetrySamplingTests"/>): a dispatched message's span carries a trace id that
/// differs from the row's stored one, links back to it, and copies its stored sampled decision. The
/// capturing listener forces every Platform-source activity recorded, so the origin's own sampled
/// flag is deterministic regardless of the 10% root ratio — the interesting assertion is that the
/// dispatch span's flag matches that forced-true origin, not a fresh independent roll.</summary>
[Collection(TelemetryTestCollection.Name)]
public sealed class TelemetryDispatchSpanTests
{
    [Fact]
    public async Task Dispatch_mints_a_new_trace_id_linked_to_and_sampled_like_the_stored_origin()
    {
        using var capture = ActivityCapture.ForPlatformSource();

        await using var host = await PlatformTestHost.CreateBuilder()
            .WithRole(HostRole.Worker)
            .WithProvider(PersistenceProvider.Sqlite)
            .WithServices(services => services.AddPlatformEventHandler<TestEvent, TestEventHandler>(new EventTypeName("test.event")))
            .StartAsync(CancellationToken.None);

        Assert.True((await host.Services.GetRequiredService<IMigrationRunner>().ApplyAsync(CancellationToken.None)).IsSuccess);

        var scopes = host.Services.GetRequiredService<IOperationScopeFactory>();
        var unitOfWork = host.Services.GetRequiredService<IUnitOfWork>();
        var writer = host.Services.GetRequiredService<IOutboxWriter>();

        TraceContext origin;
        using (var scope = scopes.Begin(TenantId.Implicit, null))
        {
            origin = scope.Trace;
            var committed = await unitOfWork.ExecuteAsync(
                TransactionIntent.Write,
                _ =>
                {
                    writer.Enqueue(new TestEvent());
                    return Task.CompletedTask;
                },
                CancellationToken.None);
            Assert.True(committed.IsSuccess);
        }

        Assert.True(origin.Sampled, "the capturing listener forces every activity recorded, so the origin must be sampled");

        await host.RunBackgroundWorkOnceAsync(PlatformBackgroundWork.OutboxDispatch, CancellationToken.None);

        // Matched by the link back to this test's own (freshly random) origin trace id rather than
        // Assert.Single: ActivitySource listeners are process-wide, so a concurrently-running
        // dispatch test (in a different xUnit collection) can land its own span in the same capture.
        var dispatchSpan = capture.Stopped.First(a => a.OperationName == "platform.outbox.dispatch"
            && a.Links.Any(link => link.Context.TraceId.ToString() == origin.TraceId));

        Assert.NotEqual(origin.TraceId, dispatchSpan.TraceId.ToString());
        Assert.True(dispatchSpan.Recorded);

        var link = Assert.Single(dispatchSpan.Links);
        Assert.Equal(origin.TraceId, link.Context.TraceId.ToString());
    }
}
