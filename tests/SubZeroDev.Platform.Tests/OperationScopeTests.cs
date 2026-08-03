using Microsoft.Extensions.DependencyInjection;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Testing;

namespace SubZeroDev.Platform.Tests;

public sealed class OperationScopeTests
{
    [Fact]
    public async Task The_three_accessors_throw_outside_a_scope()
    {
        await using var host = await PlatformTestHost.CreateBuilder().StartAsync(CancellationToken.None);

        foreach (var read in Reads(host.Services))
        {
            var thrown = Assert.Throws<PlatformContractViolationException>(read);
            Assert.Equal("NoAmbientOperationScope", thrown.Error.Code);
        }
    }

    [Fact]
    public async Task An_originating_scope_starts_a_root_whose_trace_id_is_the_correlation()
    {
        await using var host = await PlatformTestHost.CreateBuilder().StartAsync(CancellationToken.None);
        var factory = host.Services.GetRequiredService<IOperationScopeFactory>();

        using var scope = factory.Begin(TenantId.Implicit, null);

        Assert.Equal(scope.Trace.TraceId, scope.Correlation.TraceId);
        Assert.Equal(TenantId.Implicit, scope.Tenant);
        Assert.Null(scope.Principal);
    }

    [Fact]
    public async Task Inside_a_scope_the_accessors_return_the_scopes_values()
    {
        await using var host = await PlatformTestHost.CreateBuilder().StartAsync(CancellationToken.None);
        var factory = host.Services.GetRequiredService<IOperationScopeFactory>();

        using var scope = factory.Begin(TenantId.Implicit, null);

        Assert.Equal(scope.Correlation, host.Services.GetRequiredService<ICurrentCorrelation>().Current);
        Assert.Equal(TenantId.Implicit, host.Services.GetRequiredService<ICurrentTenant>().Current);
        Assert.Null(host.Services.GetRequiredService<ICurrentPrincipal>().Current);
    }

    [Fact]
    public async Task An_established_scope_keeps_a_correlation_that_differs_from_its_trace()
    {
        await using var host = await PlatformTestHost.CreateBuilder().StartAsync(CancellationToken.None);
        var factory = host.Services.GetRequiredService<IOperationScopeFactory>();

        var trace = new TraceContext("00-1111111111111111111111111111aaaa-2222222222222222-01", null);
        var origin = new CorrelationId("3333333333333333333333333333bbbb");

        using var scope = factory.Begin(trace, origin, TenantId.Implicit, null);

        // The one boundary where the two are permitted to differ. Dispatch relies on it.
        Assert.Equal(origin, scope.Correlation);
        Assert.NotEqual(scope.Trace.TraceId, scope.Correlation.TraceId);
    }

    [Fact]
    public async Task Disposing_a_scope_restores_the_one_it_replaced()
    {
        await using var host = await PlatformTestHost.CreateBuilder().StartAsync(CancellationToken.None);
        var factory = host.Services.GetRequiredService<IOperationScopeFactory>();
        var correlation = host.Services.GetRequiredService<ICurrentCorrelation>();

        using var outer = factory.Begin(TenantId.Implicit, null);
        var outerCorrelation = correlation.Current;

        using (factory.Begin(TenantId.Implicit, null))
        {
            Assert.NotEqual(outerCorrelation, correlation.Current);
        }

        Assert.Equal(outerCorrelation, correlation.Current);
    }

    [Fact]
    public void A_malformed_trace_parent_never_parses_and_never_throws()
    {
        foreach (var malformed in new[] { "not-a-traceparent", string.Empty, "00-tooshort-x-01" })
        {
            Assert.False(TraceContext.TryParse(malformed, null, out _));
        }
    }

    [Fact]
    public void An_all_zero_trace_id_is_not_a_correlation()
    {
        Assert.False(CorrelationId.TryParse(new string('0', 32), out _));
        Assert.False(TraceContext.TryParse($"00-{new string('0', 32)}-2222222222222222-01", null, out _));
    }

    [Fact]
    public void Trace_flags_travel_with_the_context()
    {
        Assert.True(TraceContext.TryParse(
            "00-1111111111111111111111111111aaaa-2222222222222222-01",
            null,
            out var sampled));
        Assert.True(sampled.Sampled);

        Assert.True(TraceContext.TryParse(
            "00-1111111111111111111111111111aaaa-2222222222222222-00",
            "vendor=state",
            out var unsampled));
        Assert.False(unsampled.Sampled);
        Assert.Equal("vendor=state", unsampled.TraceState);
    }

    private static IEnumerable<Action> Reads(IServiceProvider services)
    {
        yield return () => _ = services.GetRequiredService<ICurrentCorrelation>().Current;
        yield return () => _ = services.GetRequiredService<ICurrentTenant>().Current;
        yield return () => _ = services.GetRequiredService<ICurrentPrincipal>().Current;
    }
}
