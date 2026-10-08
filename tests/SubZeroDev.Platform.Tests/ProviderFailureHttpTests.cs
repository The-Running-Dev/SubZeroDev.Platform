using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Hosting;

namespace SubZeroDev.Platform.Tests;

/// <summary>S24: a permission check during a provider outage answers "try again" over HTTP — 503 with
/// the error's code — and never forbidden, and the handler never runs.</summary>
/// <remarks>The pipeline check is never resource-scoped (I-R7), so the Organizations provider, which
/// answers only for an <c>Organization</c> resource, never reaches its store from it. The outage is a
/// provider that could not answer at the pipeline instead (<c>design/90-decisions.md</c>,
/// 2026-10-08, S24).</remarks>
public sealed class ProviderFailureHttpTests
{
    private static readonly PermissionName Permission = new("Test.Outage.Use");

    [Fact]
    public async Task S24_5_An_endpoint_whose_provider_cannot_answer_returns_503_ProviderUnavailable_and_never_runs()
    {
        var handlerRuns = 0;
        var (app, client) = await StartAsync(
            new RecordingAuditSink("provider-failure-tests", isDurable: true),
            () => handlerRuns++);

        await using (app)
        using (client)
        {
            var response = await client.GetAsync("/guarded");

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(CancellationToken.None);
            Assert.Equal(nameof(AuthorizationError.ProviderUnavailable), body.GetProperty("code").GetString());
            Assert.Equal(0, handlerRuns);
        }
    }

    [Fact]
    public async Task S24_7_A_decision_carrying_both_failures_is_answered_with_the_audit_failure_code()
    {
        var handlerRuns = 0;
        var sink = new RecordingAuditSink("provider-failure-tests", isDurable: true);
        sink.FailNextWith(_ => Result<AuditError>.Failure(AuditError.SinkUnavailable("provider-failure-tests")));
        var (app, client) = await StartAsync(sink, () => handlerRuns++);

        await using (app)
        using (client)
        {
            var response = await client.GetAsync("/guarded");

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(CancellationToken.None);
            Assert.Equal(AuditError.SinkUnavailable("provider-failure-tests").Code, body.GetProperty("code").GetString());
            Assert.Equal(0, handlerRuns);
        }
    }

    private static Task<(WebApplication App, HttpClient Client)> StartAsync(RecordingAuditSink sink, Action onHandler) =>
        WebHostUnderTest.StartAsync(
            services =>
            {
                services.AddSingleton<IAuthenticationProvider>(new StubAuthenticationProvider("provider-failure-tests"));
                services.AddSingleton<IAuditSink>(sink);
                services.AddSingleton<IPermissionCatalog>(new StubPermissionCatalog(Permission));

                // The provider's own error is not ProviderUnavailable; the answer is still the
                // normalised one.
                services.AddSingleton<IPermissionProvider>(new StubPermissionProvider(
                    "unreachable-store",
                    (_, _, _) => Result<IReadOnlySet<PermissionName>, AuthorizationError>.Failure(
                        AuthorizationError.PermissionDenied(Permission))));
            },
            composeOperatedDefaults: false,
            mapEndpoints: application => application
                .MapGet("/guarded", () =>
                {
                    onHandler();
                    return Results.Ok();
                })
                .RequiresPlatformAuthorization(Permission, feature: null));
}
