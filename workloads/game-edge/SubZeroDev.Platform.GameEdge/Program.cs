using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.GameEdge;
using SubZeroDev.Platform.Hosting;

var builder = WebApplication.CreateBuilder(args);

var options = builder.Configuration.GetSection("GameEdge").Get<GameEdgeOptions>();

// `required` binds nothing: it is a compile-time obligation on the initialiser, and the
// configuration binder does not enforce it. A section that exists but omits WorkloadBaseAddress
// therefore binds to a null Uri, and the host starts, reports liveness healthy and fails every
// forward with an unhandled 500 — which is the outcome "no default" exists to prevent, arrived at
// one request later. appsettings.json always carries the section, so a null check alone would
// never fire; each setting is checked for itself.
if (options is null
    || options.WorkloadBaseAddress is not { IsAbsoluteUri: true }
    || options.ForwardTimeout <= TimeSpan.Zero
    || options.ReadinessTimeout <= TimeSpan.Zero)
{
    throw new InvalidOperationException(
        "Configuration section 'GameEdge' is required: WorkloadBaseAddress must be an absolute URI, "
        + "and ForwardTimeout and ReadinessTimeout must both be positive.");
}

builder.Services.AddSingleton(options);

builder.Services.AddHttpClient<IGameWorkloadForwarder, GameWorkloadForwarder>();

// The probe is a singleton because the check that holds it is one, and it asks the factory for a
// client per probe rather than capturing one — see GameWorkloadProbe's own note.
builder.Services.AddHttpClient(GameWorkloadProbe.HttpClientName);
builder.Services.TryAddSingleton<IGameWorkloadProbe, GameWorkloadProbe>();
builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IHealthCheck, GameWorkloadReadinessCheck>());

// The only mandatory Platform call. Health, readiness and correlation come with it.
builder.AddPlatformWebHost();

var app = builder.Build();

// Ordinary application code, registered the way any application registers a route — there is no
// AddGameEdge.
app.MapGameWorkloadForwarding();

app.Run();

/// <summary>Exposes the entry point to <c>WebApplicationFactory&lt;Program&gt;</c>.</summary>
public partial class Program;
