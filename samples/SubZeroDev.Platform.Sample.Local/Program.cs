using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Hosting;

var builder = WebApplication.CreateBuilder(args);

// The identity-free deployment: no Identity, Organizations, Billing or Licensing package or
// project reference exists anywhere in this sample — see the .csproj. The only mandatory
// Platform call. Health, readiness and correlation come with it.
builder.AddPlatformWebHost();

var app = builder.Build();

// D5-S1.1: an adopter must be able to see, from the log alone, which shape this host claims to be.
app.Logger.LogInformation(
    "Composition profile: {CompositionProfile}",
    app.Services.GetRequiredService<PlatformOptions>().CompositionProfile);

app.MapGet("/", (ICurrentCorrelation correlation, ICurrentTenant tenant) => new
{
    correlation = correlation.Current.TraceId,
    tenant = tenant.Current.Value,
});

app.Run();

return 0;
