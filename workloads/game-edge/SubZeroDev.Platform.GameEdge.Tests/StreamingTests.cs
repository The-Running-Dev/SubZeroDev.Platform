using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.GameEdge.Tests.Support;

namespace SubZeroDev.Platform.GameEdge.Tests;

/// <summary>S41 — a route the operator lists is relayed as the workload produces it, and a workload
/// that dies after its headers leaves the caller an incomplete response rather than a finished one.
/// S42 — that abort is the operator's to see: logged and counted once, and never confused with a
/// caller who left.
/// The edge runs on real Kestrel throughout, because chunk framing and a closed connection are what
/// is being observed, and the in-memory test server has neither.</summary>
public sealed class StreamingTests
{
    private const string Route = "/console/stream";
    private const string First = "data: 1\n\n";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    /// <summary>A first-byte budget for the unreachable cases only. Windows retries a refused loopback
    /// connect for about two seconds before failing it, which races the route's 2 s budget and turns
    /// "unreachable" into "timed out" on a developer machine; the budget is not what those cases
    /// observe.</summary>
    private const string RefusalBudget = "00:00:08";

    [Fact]
    public async Task Each_emitted_piece_reaches_the_caller_before_the_workload_emits_the_next()
    {
        await using var workload = await FakeWorkload.StartAsync();
        var next = Gate();
        workload.Script = async context =>
        {
            context.Response.ContentType = "text/event-stream";
            await FakeWorkload.EmitAsync(context, First);
            await next.Task.WaitAsync(context.RequestAborted);
            await FakeWorkload.EmitAsync(context, "data: 2\n\n");
        };

        await using var edge = StartEdge(workload.BaseAddress);
        using var client = Caller(edge);

        using var response = await client.GetAsync(Route, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        await using var body = await response.Content.ReadAsStreamAsync();

        // The workload is still waiting on `next`, so this read can only complete if the edge
        // relayed the first piece on its own.
        Assert.Equal(First, await ReadTextAsync(body, First.Length).WaitAsync(Patience));
        Assert.False(next.Task.IsCompleted);

        next.SetResult();
        using var rest = new StreamReader(body);
        Assert.Equal("data: 2\n\n", await rest.ReadToEndAsync().WaitAsync(Patience));
    }

    [Fact]
    public async Task A_workload_that_dies_mid_stream_aborts_the_response_without_a_terminating_chunk()
    {
        await using var workload = await FakeWorkload.StartAsync();
        var die = Gate();
        workload.Script = DiesAfterFirst(die);

        await using var edge = StartEdge(workload.BaseAddress);
        var address = EdgeAddress(edge);

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(address.Host, address.Port);
        await using var stream = tcp.GetStream();

        var request = Encoding.ASCII.GetBytes($"GET {Route} HTTP/1.1\r\nHost: {address.Authority}\r\n\r\n");
        await stream.WriteAsync(request);

        var received = new MemoryStream();
        var buffer = new byte[4096];

        // Read until the first piece has arrived in its chunk, then let the workload die, then read
        // until the edge closes the connection.
        while (!Encoding.ASCII.GetString(received.ToArray()).Contains($"{First}\r\n", StringComparison.Ordinal))
        {
            var read = await stream.ReadAsync(buffer).AsTask().WaitAsync(Patience);
            Assert.NotEqual(0, read);
            received.Write(buffer, 0, read);
        }

        die.SetResult();
        await ReadToCloseAsync(stream, received).WaitAsync(Patience);

        var text = Encoding.ASCII.GetString(received.ToArray());
        var split = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var head = text[..split];
        var chunked = text[(split + 4)..];

        Assert.StartsWith("HTTP/1.1 200", head, StringComparison.Ordinal);
        Assert.Contains("Transfer-Encoding: chunked", head, StringComparison.OrdinalIgnoreCase);

        // The one chunk the workload produced, and nothing after it: no `0\r\n\r\n`, no edge status,
        // envelope or trailer (I-E3).
        Assert.Equal($"{First.Length:x}\r\n{First}\r\n", chunked);
        Assert.Single(workload.Requests);
    }

    [Fact]
    public async Task A_workload_that_dies_mid_stream_fails_the_callers_read_rather_than_ending_it()
    {
        await using var workload = await FakeWorkload.StartAsync();
        var die = Gate();
        workload.Script = DiesAfterFirst(die);

        await using var edge = StartEdge(workload.BaseAddress);
        using var client = Caller(edge);

        using var response = await client.GetAsync(Route, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var body = await response.Content.ReadAsStreamAsync();
        Assert.Equal(First, await ReadTextAsync(body, First.Length).WaitAsync(Patience));

        die.SetResult();

        // `HttpIOException` is an `IOException`; a clean end would return normally instead.
        await Assert.ThrowsAnyAsync<IOException>(() => body.CopyToAsync(Stream.Null).WaitAsync(Patience));
    }

    [Fact]
    public async Task A_workload_that_sends_no_headers_within_the_first_byte_budget_is_answered_504_well_inside_ForwardTimeout()
    {
        await using var workload = await FakeWorkload.StartAsync();
        workload.Script = async context =>
        {
            await Task.Delay(TimeSpan.FromSeconds(3), context.RequestAborted);
            await FakeWorkload.EmitAsync(context, First);
        };

        await using var edge = StartEdge(workload.BaseAddress);
        using var client = Caller(edge);

        var clock = Stopwatch.StartNew();
        using var response = await client.GetAsync(Route);
        clock.Stop();

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("workload_timeout", body.GetProperty("code").GetString());
        Assert.Matches("^[0-9a-f]{32}$", body.GetProperty("correlation").GetString());

        // The route's 2 s budget, not the 10 s ForwardTimeout, and not the workload's 3 s.
        Assert.InRange(clock.Elapsed, TimeSpan.FromSeconds(1.9), TimeSpan.FromSeconds(3));
        Assert.Single(workload.Requests);
    }

    [Fact]
    public async Task An_unreachable_workload_on_a_streamed_route_is_answered_503()
    {
        await using var edge = StartEdge($"http://127.0.0.1:{FindClosedPort()}", firstByteTimeout: RefusalBudget);
        using var client = Caller(edge);

        using var response = await client.GetAsync(Route);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("workload_unreachable", body.GetProperty("code").GetString());
        Assert.Matches("^[0-9a-f]{32}$", body.GetProperty("correlation").GetString());
    }

    [Fact]
    public async Task A_streamed_forward_that_fails_is_attempted_exactly_once()
    {
        // Dies after its headers: one attempt, and the body read fails rather than ending.
        await using (var workload = await FakeWorkload.StartAsync())
        {
            var die = Gate();
            workload.Script = DiesAfterFirst(die);
            var (forwarder, handler) = Forwarder(workload.BaseAddress);

            var result = await forwarder.ForwardStreamingAsync(Get(), TimeSpan.FromSeconds(2), CancellationToken.None);
            Assert.True(result.IsSuccess);
            await using (var streamed = result.Value)
            {
                Assert.Equal(First, await ReadTextAsync(streamed.Body, First.Length).WaitAsync(Patience));
                die.SetResult();
                await Assert.ThrowsAnyAsync<IOException>(() => streamed.Body.CopyToAsync(Stream.Null).WaitAsync(Patience));
            }

            Assert.Equal(1, handler.CallCount);
            Assert.Single(workload.Requests);
        }

        // Sends no headers within the budget.
        await using (var workload = await FakeWorkload.StartAsync())
        {
            workload.Script = context => Task.Delay(TimeSpan.FromSeconds(3), context.RequestAborted);
            var (forwarder, handler) = Forwarder(workload.BaseAddress);

            var result = await forwarder.ForwardStreamingAsync(Get(), TimeSpan.FromMilliseconds(300), CancellationToken.None);

            Assert.False(result.IsSuccess);
            Assert.Equal("workload_timeout", result.Error.Code);
            Assert.Equal(1, handler.CallCount);
            Assert.Single(workload.Requests);
        }

        // Unreachable.
        {
            var (forwarder, handler) = Forwarder($"http://127.0.0.1:{FindClosedPort()}");

            var result = await forwarder.ForwardStreamingAsync(Get(), TimeSpan.Parse(RefusalBudget), CancellationToken.None);

            Assert.False(result.IsSuccess);
            Assert.Equal("workload_unreachable", result.Error.Code);
            Assert.Equal(1, handler.CallCount);
        }
    }

    [Fact]
    public async Task An_unlisted_route_is_buffered_when_another_route_streams()
    {
        await using var workload = await FakeWorkload.StartAsync();
        var next = Gate();
        workload.Script = async context =>
        {
            if (context.Request.Path == "/state")
            {
                // #106: dies after its headers on a buffered route.
                await FakeWorkload.EmitAsync(context, "{\"partial\":");
                context.Abort();
                return;
            }

            context.Response.ContentType = "text/event-stream";
            await FakeWorkload.EmitAsync(context, First);
            await next.Task.WaitAsync(context.RequestAborted);
            await FakeWorkload.EmitAsync(context, "data: 2\n\n");
        };

        await using var edge = StartEdge(workload.BaseAddress);
        using var client = Caller(edge);

        // `/console/streamer` shares characters with the listed prefix but not a segment boundary,
        // so it is buffered: nothing reaches the caller — not even the status — until the workload
        // has finished.
        var pending = client.GetAsync("/console/streamer", HttpCompletionOption.ResponseHeadersRead);
        var winner = await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(1)));
        Assert.NotSame(pending, winner);

        next.SetResult();
        using var buffered = await pending.WaitAsync(Patience);
        Assert.Equal(HttpStatusCode.OK, buffered.StatusCode);
        Assert.Equal(First + "data: 2\n\n", await buffered.Content.ReadAsStringAsync());

        using var state = await client.GetAsync("/state");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, state.StatusCode);
        var body = JsonDocument.Parse(await state.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("workload_unreachable", body.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("/console/stream", true)]
    [InlineData("/console/stream/42", true)]
    [InlineData("/console/streamer", false)]
    [InlineData("/Console/stream", false)]
    [InlineData("/console", false)]
    [InlineData("/state", false)]
    public void A_path_matches_a_prefix_only_on_a_segment_boundary_and_ordinally(string path, bool streams)
    {
        StreamingRoute[] routes = [new() { PathPrefix = Route, FirstByteTimeout = TimeSpan.FromSeconds(2) }];

        Assert.Equal(streams, StreamingRoutes.Match(routes, path) is not null);
    }

    [Fact]
    public async Task A_stream_held_open_past_ForwardTimeout_completes_with_every_event()
    {
        await using var workload = await FakeWorkload.StartAsync();
        workload.Script = async context =>
        {
            context.Response.ContentType = "text/event-stream";
            for (var index = 1; index <= 12; index++)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), context.RequestAborted);
                await FakeWorkload.EmitAsync(context, $"data: {index}\n\n");
            }
        };

        await using var edge = StartEdge(workload.BaseAddress);
        using var client = Caller(edge);

        using var response = await client.GetAsync(Route, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        var text = await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(string.Concat(Enumerable.Range(1, 12).Select(index => $"data: {index}\n\n")), text);
    }

    [Fact]
    public async Task A_caller_that_disconnects_mid_stream_releases_the_workload_without_an_error_log()
    {
        await using var workload = await FakeWorkload.StartAsync();
        workload.Script = async context =>
        {
            context.Response.ContentType = "text/event-stream";
            await FakeWorkload.EmitAsync(context, First);
            await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
        };

        var logs = new CapturingLoggerProvider();
        await using var edge = StartEdge(workload.BaseAddress, logs, environment: ObservedEnvironment);

        // No drain on dispose: the connection closes when the response is let go, as a browser tab
        // closing would.
        using (var client = new HttpClient(new SocketsHttpHandler { MaxResponseDrainSize = 0 }) { BaseAddress = EdgeAddress(edge) })
        {
            using var response = await client.GetAsync(Route, HttpCompletionOption.ResponseHeadersRead);
            await using var body = await response.Content.ReadAsStreamAsync();
            Assert.Equal(First, await ReadTextAsync(body, First.Length).WaitAsync(Patience));
        }

        await workload.ScriptAborted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // Let the edge's handler finish unwinding before reading what it logged.
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        Assert.Contains(logs.Entries, entry => entry.Category.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Entries, entry => entry.Level >= LogLevel.Error);
    }

    /// <summary>S41.9, before the commit point: a caller who leaves while the workload has not yet
    /// sent headers is not the workload's failure either. The budget is long enough that the
    /// first-byte timeout is not what ends the exchange.</summary>
    [Fact]
    public async Task A_caller_that_disconnects_before_the_workloads_headers_releases_the_workload_without_an_error_log()
    {
        await using var workload = await FakeWorkload.StartAsync();
        var reached = Gate();
        workload.Script = async context =>
        {
            reached.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
        };

        var logs = new CapturingLoggerProvider();
        await using var edge = StartEdge(
            workload.BaseAddress, logs, firstByteTimeout: RefusalBudget, environment: ObservedEnvironment);

        using (var client = new HttpClient(new SocketsHttpHandler { MaxResponseDrainSize = 0 }) { BaseAddress = EdgeAddress(edge) })
        using (var leave = new CancellationTokenSource())
        {
            var pending = client.GetAsync(Route, HttpCompletionOption.ResponseHeadersRead, leave.Token);
            await reached.Task.WaitAsync(Patience);
            await leave.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }

        await workload.ScriptAborted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await Task.Delay(TimeSpan.FromMilliseconds(300));
        Assert.Contains(logs.Entries, entry => entry.Category.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Entries, entry => entry.Level >= LogLevel.Error);
    }

    /// <summary>S42.1, S42.3 and S42.4 (I-E5). A caller who leaves mid-stream and a stream that ends
    /// cleanly come first, so the single record and single measurement at the end are the abort's
    /// alone.</summary>
    [Fact]
    public async Task An_abort_is_logged_and_counted_once_and_a_caller_disconnect_is_neither()
    {
        await using var workload = await FakeWorkload.StartAsync();
        var logs = new CapturingLoggerProvider();
        await using var edge = StartEdge(workload.BaseAddress, logs, environment: ObservedEnvironment);
        using var aborts = new AbortMeasurements(edge);

        workload.Script = async context =>
        {
            context.Response.ContentType = "text/event-stream";
            await FakeWorkload.EmitAsync(context, First);
            await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
        };
        using (var leaving = new HttpClient(new SocketsHttpHandler { MaxResponseDrainSize = 0 }) { BaseAddress = EdgeAddress(edge) })
        {
            using var response = await leaving.GetAsync(Route, HttpCompletionOption.ResponseHeadersRead);
            await using var body = await response.Content.ReadAsStreamAsync();
            Assert.Equal(First, await ReadTextAsync(body, First.Length).WaitAsync(Patience));
        }

        await workload.ScriptAborted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        using var client = Caller(edge);
        workload.Script = async context =>
        {
            context.Response.ContentType = "text/event-stream";
            await FakeWorkload.EmitAsync(context, First);
        };
        Assert.Equal(First, await client.GetStringAsync(Route).WaitAsync(Patience));

        var die = Gate();
        workload.Script = DiesAfterFirst(die);
        using var request = new HttpRequestMessage(HttpMethod.Get, Route);
        request.Headers.Add("traceparent", $"00-{TraceId}-00f067aa0ba902b7-01");
        using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead))
        {
            await using var body = await response.Content.ReadAsStreamAsync();
            Assert.Equal(First, await ReadTextAsync(body, First.Length).WaitAsync(Patience));
            die.SetResult();
            await Assert.ThrowsAnyAsync<IOException>(() => body.CopyToAsync(Stream.Null).WaitAsync(Patience));
        }

        var record = await SettledSingleAsync(logs, entry => entry.EventId.Id == 1101);
        Assert.Equal("EdgeStreamAborted", record.EventId.Name);
        Assert.Equal(LogLevel.Warning, record.Level);
        Assert.Equal("SubZeroDev.Platform.GameEdge", record.Category);
        Assert.Equal(Route, record.Value("Route"));
        Assert.Equal(TraceId, record.Value("Correlation"));
        Assert.Equal("workload_unreachable", record.Value("Code"));
        Assert.True(Assert.IsType<long>(record.Value("BytesForwarded")) >= First.Length);
        Assert.False(string.IsNullOrEmpty(Assert.IsType<string>(record.Value("ExceptionType"))));

        // The exception travels as its type name only: attached, its message would reach every sink.
        Assert.Null(record.Exception);

        var measurement = Assert.Single(aborts.Taken);
        Assert.Equal(1, measurement.Value);
        Assert.Equal(Route, measurement.Tags["route"]);
        Assert.Equal("workload_unreachable", measurement.Tags["code"]);

        var edgeRecords = logs.Entries.Where(entry => entry.Category == "SubZeroDev.Platform.GameEdge" && entry.Level > LogLevel.Debug);
        Assert.Same(record, Assert.Single(edgeRecords));
    }

    /// <summary>S42.2. The records Platform and the edge write name the configured prefix; the
    /// request's path and query, which can carry a session or a token, appear in none of them. The
    /// framework's own request and outbound-call records do carry the path, which is the access log's
    /// job, and the level that governs them is the operator's.</summary>
    [Fact]
    public async Task An_aborted_stream_is_recorded_without_the_requests_path_or_query()
    {
        await using var workload = await FakeWorkload.StartAsync();
        var die = Gate();
        workload.Script = DiesAfterFirst(die);

        var logs = new CapturingLoggerProvider();
        await using var edge = StartEdge(workload.BaseAddress, logs, environment: ObservedEnvironment);
        using var client = Caller(edge);

        using (var response = await client.GetAsync("/console/stream/7?token=secret", HttpCompletionOption.ResponseHeadersRead))
        {
            await using var body = await response.Content.ReadAsStreamAsync();
            Assert.Equal(First, await ReadTextAsync(body, First.Length).WaitAsync(Patience));
            die.SetResult();
            await Assert.ThrowsAnyAsync<IOException>(() => body.CopyToAsync(Stream.Null).WaitAsync(Patience));
        }

        var record = await SettledSingleAsync(logs, entry => entry.EventId.Id == 1101);
        Assert.Equal(Route, record.Value("Route"));

        string[] forbidden = ["/console/stream/7", "token", "secret"];
        var ours = logs.Entries.Where(entry => entry.Category.StartsWith("SubZeroDev.", StringComparison.Ordinal)).ToList();
        Assert.Contains(record, ours);
        var leaks = ours
            .Where(entry => forbidden.Any(entry.Text.Contains))
            .Select(entry => $"{entry.Level} {entry.Category}: {entry.Text}")
            .ToList();
        Assert.Empty(leaks);
    }

    /// <summary>S42.5. No S41 outcome on a streamed route — a clean end, a caller who leaves, a
    /// workload that dies after its headers, sends none in time, or is gone — is an unhandled failure
    /// that reaches Platform's error envelope.</summary>
    [Fact]
    public async Task No_streamed_route_outcome_reaches_the_error_envelope()
    {
        var workload = await FakeWorkload.StartAsync();
        var logs = new CapturingLoggerProvider();
        await using var edge = StartEdge(workload.BaseAddress, logs, environment: ObservedEnvironment);
        using var client = Caller(edge);

        try
        {
            workload.Script = async context =>
            {
                context.Response.ContentType = "text/event-stream";
                await FakeWorkload.EmitAsync(context, First);
            };
            Assert.Equal(First, await client.GetStringAsync(Route).WaitAsync(Patience));

            workload.Script = async context =>
            {
                context.Response.ContentType = "text/event-stream";
                await FakeWorkload.EmitAsync(context, First);
                await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
            };
            using (var leaving = new HttpClient(new SocketsHttpHandler { MaxResponseDrainSize = 0 }) { BaseAddress = EdgeAddress(edge) })
            {
                using var response = await leaving.GetAsync(Route, HttpCompletionOption.ResponseHeadersRead);
                await using var body = await response.Content.ReadAsStreamAsync();
                Assert.Equal(First, await ReadTextAsync(body, First.Length).WaitAsync(Patience));
            }

            await workload.ScriptAborted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var die = Gate();
            workload.Script = DiesAfterFirst(die);
            using (var response = await client.GetAsync(Route, HttpCompletionOption.ResponseHeadersRead))
            {
                await using var body = await response.Content.ReadAsStreamAsync();
                Assert.Equal(First, await ReadTextAsync(body, First.Length).WaitAsync(Patience));
                die.SetResult();
                await Assert.ThrowsAnyAsync<IOException>(() => body.CopyToAsync(Stream.Null).WaitAsync(Patience));
            }

            workload.Script = async context => await Task.Delay(TimeSpan.FromSeconds(3), context.RequestAborted);
            using (var response = await client.GetAsync(Route))
            {
                Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
            }
        }
        finally
        {
            await workload.DisposeAsync();
        }

        // Gone. On Windows a refused connect can outlast the 2 s budget and be answered 504 rather
        // than 503; either is the edge's own answer, and neither is the envelope's 500.
        using (var response = await client.GetAsync(Route))
        {
            Assert.Contains(response.StatusCode, new[] { HttpStatusCode.ServiceUnavailable, HttpStatusCode.GatewayTimeout });
        }

        await Task.Delay(TimeSpan.FromMilliseconds(300));
        Assert.Contains(logs.Entries, entry => entry.Category.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
        Assert.DoesNotContain(
            logs.Entries,
            entry => entry.Category.EndsWith(".ErrorEnvelopeMiddleware", StringComparison.Ordinal) && entry.Level >= LogLevel.Error);
    }

    private static Func<HttpContext, Task> DiesAfterFirst(TaskCompletionSource die) => async context =>
    {
        context.Response.ContentType = "text/event-stream";
        await FakeWorkload.EmitAsync(context, First);
        await die.Task.WaitAsync(context.RequestAborted);

        // Destroys the socket: the chunked body ends with no terminating chunk.
        context.Abort();
    };

    /// <summary>The tests that read the edge's logs run outside Development. There the framework's
    /// exception page sits inside Platform's error envelope and absorbs an exception raised by a
    /// client abort at Debug, so an exception escaping the edge would never reach the envelope's
    /// Error log and an assertion against it could not fail.</summary>
    private const string ObservedEnvironment = "Production";

    private const string TraceId = "4bf92f3577b34da6a3ce929d0e0e4736";

    /// <summary>Waits for the first record that matches, then long enough for a second to land if
    /// the edge were to write one, and returns the only one.</summary>
    private static async Task<LogEntry> SettledSingleAsync(CapturingLoggerProvider logs, Func<LogEntry, bool> match)
    {
        var deadline = Stopwatch.StartNew();
        while (!logs.Entries.Any(match) && deadline.Elapsed < Patience)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20));
        }

        await Task.Delay(TimeSpan.FromMilliseconds(300));
        return Assert.Single(logs.Entries, entry => match(entry));
    }

    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static WebApplicationFactory<Program> StartEdge(
        string workloadBaseAddress,
        ILoggerProvider? logs = null,
        string firstByteTimeout = "00:00:02",
        string environment = "Development")
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.UseSetting("GameEdge:WorkloadBaseAddress", workloadBaseAddress);
            builder.UseSetting("GameEdge:ForwardTimeout", "00:00:10");
            builder.UseSetting("GameEdge:ReadinessTimeout", "00:00:01");
            builder.UseSetting("GameEdge:StreamingRoutes:0:PathPrefix", Route);
            builder.UseSetting("GameEdge:StreamingRoutes:0:FirstByteTimeout", firstByteTimeout);
            if (logs is not null)
            {
                // Platform composes Serilog with `writeToProviders: false`, so a provider added to the
                // logging builder is never written to. Replacing the factory after the host's own
                // registrations is what makes every `ILogger<T>` — the error middleware's included —
                // reach the capture.
                builder.ConfigureTestServices(services => services.AddSingleton(
                    LoggerFactory.Create(logging => logging.AddProvider(logs).SetMinimumLevel(LogLevel.Trace))));
            }
        });

        factory.UseKestrel(0);
        factory.StartServer();
        return factory;
    }

    private static Uri EdgeAddress(WebApplicationFactory<Program> edge) =>
        new(edge.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());

    private static HttpClient Caller(WebApplicationFactory<Program> edge) =>
        new() { BaseAddress = EdgeAddress(edge), Timeout = TimeSpan.FromSeconds(60) };

    private static (GameWorkloadForwarder Forwarder, CountingHandler Handler) Forwarder(string workloadBaseAddress)
    {
        var handler = new CountingHandler(new SocketsHttpHandler());
        var forwarder = new GameWorkloadForwarder(
            new HttpClient(handler),
            new GameEdgeOptions
            {
                WorkloadBaseAddress = new Uri(workloadBaseAddress),
                ForwardTimeout = TimeSpan.FromSeconds(10),
                ReadinessTimeout = TimeSpan.FromSeconds(1),
            });
        return (forwarder, handler);
    }

    private static ForwardedRequest Get()
    {
        TraceContext.TryParse("00-0123456789abcdef0123456789abcdef-0123456789abcdef-01", null, out var trace);
        return new ForwardedRequest(HttpMethod.Get, Route, ReadOnlyMemory<byte>.Empty, null, trace);
    }

    private static async Task<string> ReadTextAsync(Stream body, int length)
    {
        var buffer = new byte[length];
        await body.ReadExactlyAsync(buffer);
        return Encoding.UTF8.GetString(buffer);
    }

    /// <summary>Reads until the peer closes. A reset counts as closed: either way nothing more
    /// arrives, which is what is being observed.</summary>
    private static async Task ReadToCloseAsync(Stream stream, MemoryStream received)
    {
        var buffer = new byte[4096];
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer)) > 0)
            {
                received.Write(buffer, 0, read);
            }
        }
        catch (IOException)
        {
        }
    }

    private static int FindClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class CountingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed record LogEntry(
        string Category,
        LogLevel Level,
        EventId EventId,
        string Message,
        IReadOnlyList<KeyValuePair<string, object?>> State,
        Exception? Exception)
    {
        public object? Value(string key) => State.Single(pair => pair.Key == key).Value;

        /// <summary>Everything a sink could write: the message, each state value and the exception.</summary>
        public string Text =>
            string.Join(" | ", [Message, .. State.Select(pair => $"{pair.Key}={pair.Value}"), Exception?.ToString() ?? string.Empty]);
    }

    /// <summary>Listens to one host's abort counter only: the meter comes from that host's
    /// <see cref="IMeterFactory"/>, so another test's edge in the same process is not heard.</summary>
    private sealed class AbortMeasurements : IDisposable
    {
        private readonly MeterListener _listener = new();

        public AbortMeasurements(WebApplicationFactory<Program> edge)
        {
            var scope = edge.Services.GetRequiredService<IMeterFactory>();
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter.Scope, scope)
                    && instrument.Meter.Name == PlatformTelemetry.MeterName
                    && instrument.Name == "subzerodev.edge.stream.aborts")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
                Taken.Enqueue((value, tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value))));
            _listener.Start();
        }

        public ConcurrentQueue<(long Value, Dictionary<string, object?> Tags)> Taken { get; } = new();

        public void Dispose() => _listener.Dispose();
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

        public void Dispose()
        {
        }

        private sealed class Logger(CapturingLoggerProvider owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                owner.Entries.Enqueue(new LogEntry(
                    category,
                    logLevel,
                    eventId,
                    formatter(state, exception),
                    // Copied now: the framework's request records read the request lazily, and it
                    // is gone by the time a test looks.
                    (state as IEnumerable<KeyValuePair<string, object?>>)?.ToList() ?? [],
                    exception));
        }
    }
}
