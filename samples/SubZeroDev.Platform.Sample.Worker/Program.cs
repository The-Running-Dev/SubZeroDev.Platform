using Microsoft.Extensions.DependencyInjection;
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

app.Run();

return 0;
