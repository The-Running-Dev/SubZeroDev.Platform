using SubZeroDev.Platform.Hosting;

var builder = WebApplication.CreateBuilder(args);

// The worker is the same bootstrap with the product HTTP surface omitted. It maps no endpoints;
// the listener exists for its probes and nothing else.
builder.AddPlatformWorkerHost();

var app = builder.Build();

app.Run();
