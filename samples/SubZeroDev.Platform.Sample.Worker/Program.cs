using Microsoft.Extensions.DependencyInjection;
using SubZeroDev.Platform.Hosting;
using SubZeroDev.Platform.Persistence;

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

var app = builder.Build();

app.Run();

return 0;
