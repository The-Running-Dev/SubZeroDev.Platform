using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SubZeroDev.Platform.Abstractions;

namespace SubZeroDev.Platform.Tests;

/// <summary>The assertions that are about HTTP go over HTTP. Wire status, body narrowing and the
/// envelope are all things a caller sees, so testing them around the pipeline would prove nothing.</summary>
public sealed class HttpProbeTests
{
    [Fact]
    public async Task Degraded_returns_success_and_unhealthy_returns_failure()
    {
        var (degradedApp, degradedClient) = await WebHostUnderTest.StartAsync(services =>
            services.AddSingleton<IHealthCheck>(
                new StubHealthCheck("check", HealthCheckKind.Readiness, HealthStatus.Degraded)));

        var (unhealthyApp, unhealthyClient) = await WebHostUnderTest.StartAsync(services =>
            services.AddSingleton<IHealthCheck>(
                new StubHealthCheck("check", HealthCheckKind.Readiness, HealthStatus.Unhealthy)));

        try
        {
            var degraded = await degradedClient.GetAsync("/health/ready", CancellationToken.None);
            var unhealthy = await unhealthyClient.GetAsync("/health/ready", CancellationToken.None);

            // Degraded means "take traffic, something needs attention". Mapping it to failure would
            // drain a host whose optional provider is down.
            Assert.Equal(HttpStatusCode.OK, degraded.StatusCode);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, unhealthy.StatusCode);

            var degradedBody = await degraded.Content.ReadFromJsonAsync<JsonElement>(CancellationToken.None);
            var unhealthyBody = await unhealthy.Content.ReadFromJsonAsync<JsonElement>(CancellationToken.None);

            Assert.Equal("Degraded", degradedBody.GetProperty("status").GetString());
            Assert.Equal("Unhealthy", unhealthyBody.GetProperty("status").GetString());
            Assert.Equal(
                degradedBody.GetProperty("checks").GetArrayLength(),
                unhealthyBody.GetProperty("checks").GetArrayLength());
        }
        finally
        {
            await degradedApp.DisposeAsync();
            await unhealthyApp.DisposeAsync();
        }
    }

    [Fact]
    public async Task Liveness_enumerates_the_registered_checks()
    {
        var (app, client) = await WebHostUnderTest.StartAsync(services =>
            services.AddSingleton<IHealthCheck>(
                new StubHealthCheck("process", HealthCheckKind.Liveness, HealthStatus.Healthy)));

        try
        {
            var response = await client.GetAsync("/health/live", CancellationToken.None);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(CancellationToken.None);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("process", body.GetProperty("checks")[0].GetProperty("name").GetString());
            Assert.DoesNotContain(
                "platform.database",
                body.ToString(),
                StringComparison.Ordinal);
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_loopback_caller_gets_full_detail_and_the_status_is_unchanged_by_narrowing()
    {
        var (app, client) = await WebHostUnderTest.StartAsync(services =>
            services.AddSingleton<IHealthCheck>(
                new StubHealthCheck("check", HealthCheckKind.Readiness, HealthStatus.Degraded)));

        try
        {
            var response = await client.GetAsync("/health/ready", CancellationToken.None);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(CancellationToken.None);
            var entry = body.GetProperty("checks")[0];

            // The tests run over loopback, which is the full-detail case by design.
            Assert.Equal("Degraded", body.GetProperty("status").GetString());
            Assert.Equal("stub detail", entry.GetProperty("detail").GetString());
            Assert.True(entry.TryGetProperty("data", out _));
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task An_unhandled_failure_returns_a_code_and_a_correlation_and_no_exception_text()
    {
        var (app, client) = await WebHostUnderTest.StartAsync();

        try
        {
            var response = await client.GetAsync("/boom", CancellationToken.None);
            var raw = await response.Content.ReadAsStringAsync(CancellationToken.None);
            var body = JsonSerializer.Deserialize<JsonElement>(raw);

            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.Equal("UnhandledRequestFailure", body.GetProperty("code").GetString());
            Assert.Equal(32, body.GetProperty("correlation").GetString()!.Length);

            Assert.DoesNotContain("secret detail", raw, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("InvalidOperationException", raw, StringComparison.Ordinal);
            Assert.DoesNotContain("at SubZeroDev", raw, StringComparison.Ordinal);
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task An_inbound_trace_parent_becomes_the_correlation()
    {
        var (app, client) = await WebHostUnderTest.StartAsync();

        try
        {
            const string TraceId = "4bf92f3577b34da6a3ce929d0e0e4736";
            using var request = new HttpRequestMessage(HttpMethod.Get, "/");
            request.Headers.Add("traceparent", $"00-{TraceId}-00f067aa0ba902b7-01");

            var response = await client.SendAsync(request, CancellationToken.None);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(CancellationToken.None);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(TraceId, body.GetProperty("correlation").GetString());
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_malformed_trace_parent_gets_a_fresh_root_rather_than_a_rejection()
    {
        var (app, client) = await WebHostUnderTest.StartAsync();

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/");
            request.Headers.Add("traceparent", "not-a-traceparent");

            var response = await client.SendAsync(request, CancellationToken.None);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(CancellationToken.None);

            // A broken upstream header is not the caller's fault.
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(32, body.GetProperty("correlation").GetString()!.Length);
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task The_ambient_tenant_inside_a_request_is_the_implicit_tenant()
    {
        var (app, client) = await WebHostUnderTest.StartAsync();

        try
        {
            var body = await client.GetFromJsonAsync<JsonElement>("/", CancellationToken.None);

            Assert.Equal(TenantId.Implicit.Value, body.GetProperty("tenant").GetGuid());
        }
        finally
        {
            await app.DisposeAsync();
        }
    }
}
