using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.GameEdge;
using SubZeroDev.Platform.Hosting;

var builder = WebApplication.CreateBuilder(args);

var options = builder.Configuration.GetSection("GameEdge").Get<GameEdgeOptions>()
    ?? throw new InvalidOperationException(
        "Configuration section 'GameEdge' is required: WorkloadBaseAddress, ForwardTimeout and "
        + "LivenessTimeout must all be supplied.");
builder.Services.AddSingleton(options);

builder.Services.AddHttpClient<IGameWorkloadForwarder, GameWorkloadForwarder>();
builder.Services.AddHttpClient<IGameWorkloadProbe, GameWorkloadProbe>();
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
