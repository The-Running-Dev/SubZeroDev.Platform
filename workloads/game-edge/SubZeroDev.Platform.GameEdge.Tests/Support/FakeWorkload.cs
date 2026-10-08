using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SubZeroDev.Platform.GameEdge.Tests.Support;

/// <summary>A stand-in for the Node workload: a real Kestrel process on loopback, so the forwarder's
/// outbound HTTP is exercised for real rather than mocked. Records every non-<c>/livez</c>,
/// non-<c>/readyz</c> request it receives, which is what S7.7 and S10.5 check against.</summary>
internal sealed class FakeWorkload : IAsyncDisposable
{
    private readonly WebApplication _app;

    public List<RecordedRequest> Requests { get; } = [];

    public string BaseAddress { get; }

    public int LivenessStatus { get; set; } = 200;

    public int ReadinessStatus { get; set; } = 200;

    public int ResponseStatus { get; set; } = 200;

    public byte[] ResponseBody { get; set; } = [];

    public string? ResponseContentType { get; set; }

    /// <summary>When set, a non-<c>/livez</c> request accepts the connection and never answers,
    /// until the caller's own token cancels it — S7.6's "accepts the connection and never answers".</summary>
    public bool Hang { get; set; }

    /// <summary>When set, answers every recorded request in place of the fixed response, so a test can
    /// emit a body piece by piece (<see cref="EmitAsync"/>), hold it open, or destroy the socket
    /// partway with <see cref="HttpContext.Abort"/> — S41's streamed routes.</summary>
    public Func<HttpContext, Task>? Script { get; set; }

    /// <summary>Completes when a scripted response is aborted from under the script — the edge
    /// releasing its upstream connection, as S41.9's caller disconnect must.</summary>
    public TaskCompletionSource ScriptAborted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private FakeWorkload(WebApplication app, string baseAddress)
    {
        _app = app;
        BaseAddress = baseAddress;
    }

    public static async Task<FakeWorkload> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();

        FakeWorkload? fake = null;

        app.MapGet("/livez", () => Results.StatusCode(fake!.LivenessStatus));
        app.MapGet("/readyz", () => Results.StatusCode(fake!.ReadinessStatus));
        app.Map("/{**catchAll}", async (HttpContext context) =>
        {
            using var bodyStream = new MemoryStream();
            await context.Request.Body.CopyToAsync(bodyStream, context.RequestAborted);

            var traceParent = context.Request.Headers.TryGetValue("traceparent", out var value)
                ? value.ToString()
                : null;

            fake!.Requests.Add(new RecordedRequest(
                context.Request.Method,
                context.Request.Path + context.Request.QueryString,
                bodyStream.ToArray(),
                traceParent));

            if (fake.Script is { } script)
            {
                using var registration = context.RequestAborted.Register(() => fake.ScriptAborted.TrySetResult());
                try
                {
                    await script(context);
                }
                catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
                {
                }

                return;
            }

            if (fake.Hang)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
                return;
            }

            context.Response.StatusCode = fake.ResponseStatus;
            if (fake.ResponseContentType is { } contentType)
            {
                context.Response.ContentType = contentType;
            }

            await context.Response.Body.WriteAsync(fake.ResponseBody, context.RequestAborted);
        });

        await app.StartAsync();

        var addresses = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses;
        var baseAddress = addresses.First();

        fake = new FakeWorkload(app, baseAddress);
        return fake;
    }

    /// <summary>Writes <paramref name="text"/> and flushes it, so it leaves the workload as one piece
    /// rather than waiting on the next.</summary>
    public static async Task EmitAsync(HttpContext context, string text)
    {
        await context.Response.WriteAsync(text, context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync().ConfigureAwait(false);
}

internal sealed record RecordedRequest(string Method, string PathAndQuery, byte[] Body, string? TraceParent);
