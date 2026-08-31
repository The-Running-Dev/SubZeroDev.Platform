using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Hosting;
using SubZeroDev.Platform.Persistence;
using SubZeroDev.Platform.Sample.Web;

var builder = WebApplication.CreateBuilder(args);

// Migrate mode is a one-shot command, not a third host role — it exits before
// AddPlatformWorkerHost ever runs, and never serves HTTP or probes.
if (args is ["migrate"])
{
    return await builder.RunPlatformMigrateModeAsync(CancellationToken.None);
}

// The worker is the same bootstrap with the product HTTP surface omitted. It maps no endpoints;
// the listener exists for its probes and nothing else.
builder.AddPlatformWorkerHost();

// Same store as the web role — Database and PendingMigrations readiness share the one connection
// string both roles are configured with.
builder.Services.AddPlatformPersistence();

// The worker is the dispatching role: this is the one place the handler is actually constructed,
// so a missing constructor dependency aborts worker startup here rather than surfacing mid-dispatch.
builder.Services.AddPlatformEventHandler<OrderPlaced, OrderPlacedHandler>(SampleEventTypes.OrderPlaced);

var app = builder.Build();

// D5-S1.1: an adopter must be able to see, from the log alone, which shape this host claims to be.
app.Logger.LogInformation(
    "Composition profile: {CompositionProfile}",
    app.Services.GetRequiredService<PlatformOptions>().CompositionProfile);

app.Run();

return 0;

// Administration remains a library call in D3: a product can compose recovery into its own
// authenticated operator workflow, but Platform adds neither a route nor a console command.
internal static class OutboxAdministrationDemonstration
{
    internal static async Task ApplyAsync(
        IOutboxAdministration administration,
        IReadOnlyCollection<OutboxMessageId> messageIds,
        EventTypeName eventType,
        CancellationToken cancellationToken)
    {
        await administration.RedriveAsync(messageIds, cancellationToken);
        await administration.DiscardByTypeAsync(eventType, "Operator retired the poisoned messages.", cancellationToken);
    }
}
