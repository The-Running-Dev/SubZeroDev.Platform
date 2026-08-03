using Microsoft.Extensions.DependencyInjection;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Hosting;
using SubZeroDev.Platform.Sample.Web;

var builder = WebApplication.CreateBuilder(args);

// Modules are ordinary registrations, and they go on before the standard call: a module
// contributes services, and nothing can be added once the container is built.
builder.Services.AddSingleton<IPlatformModule, CatalogueModule>();
builder.Services.AddSingleton<IPlatformModule, OrdersModule>();

// The only Platform call. Health, readiness and correlation come with it — a second mandatory call
// would be the bespoke wiring the brief's definition of done names as failure.
builder.AddPlatformWebHost();

var app = builder.Build();

app.MapGet("/", (ICurrentCorrelation correlation, ICurrentTenant tenant) => new
{
    correlation = correlation.Current.TraceId,
    tenant = tenant.Current.Value,
});

// Exists to be called: an unhandled failure must return an envelope carrying the correlation and
// nothing else, which is only demonstrable against something that actually throws.
app.MapGet("/boom", void () => throw new InvalidOperationException(
    "Sample failure with detail that must not reach the wire."));

app.Run();
