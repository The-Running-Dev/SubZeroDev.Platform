using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Testing;

namespace SubZeroDev.Platform.Tests;

/// <summary>S28.4 (I-U9): Core's detection site — the audit dispatcher, which is where a sink's
/// failure is detected — reports a repeated error condition once per detection. The checks that
/// observe the condition afterwards are readiness probes, and however many run, none of them
/// writes a record or a log line of its own.</summary>
/// <remarks>Serial for the same reason as <see cref="AuditInputSurfaceTests"/>: it reads the host's
/// log file back.</remarks>
[Collection(TelemetryTestCollection.Name)]
public sealed class AuditDetectionTests
{
    private const int Checks = 50;

    private static readonly Principal TestPrincipal = new(new PrincipalId("issuer-a", "alice"), PrincipalKind.Account, "Alice", null);

    [Fact]
    public async Task S28_4_A_failing_sink_is_reported_once_per_detection_however_many_checks_observe_it()
    {
        using var probe = new AuditInputSurfaceTests.Probe();
        probe.Sink.FailNextWith(_ => Result<AuditError>.Failure(AuditError.SinkUnavailable(probe.Sink.Name)));

        await using (var host = await PlatformTestHost.CreateBuilder()
            .WithSetting("Telemetry:LogDirectory", probe.LogDirectory)
            .WithServices(services => services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditSink>(probe.Sink)))
            .StartAsync(CancellationToken.None))
        {
            var scopeFactory = host.Services.GetRequiredService<IOperationScopeFactory>();
            var writer = host.Services.GetRequiredService<IAuditWriter>();

            // One detection, then many checks.
            await WriteAsync(scopeFactory, writer, expectSuccess: false);
            await AssertChecksAsync(host, HealthStatus.Degraded);
            Assert.Single(probe.Sink.Received);

            // A second failing write is a second detection, not a repeat of the first.
            await WriteAsync(scopeFactory, writer, expectSuccess: false);
            await AssertChecksAsync(host, HealthStatus.Degraded);
            Assert.Equal(2, probe.Sink.Received.Count);

            // Recovery is itself detected by the next write, not by a check.
            probe.Sink.FailNextWith(_ => Result<AuditError>.Success());
            await WriteAsync(scopeFactory, writer, expectSuccess: true);
            await AssertChecksAsync(host, HealthStatus.Healthy);
            Assert.Equal(3, probe.Sink.Received.Count);

            await probe.SealAsync(host.Services);
        }

        var failureLines = (await probe.ReadLogAsync())
            .Split('\n')
            .Count(line => line.Contains("failed to write event", StringComparison.Ordinal)
                && line.Contains(probe.Sink.Name, StringComparison.Ordinal));
        Assert.Equal(2, failureLines);
    }

    private static async Task WriteAsync(IOperationScopeFactory scopeFactory, IAuditWriter writer, bool expectSuccess)
    {
        using (scopeFactory.Begin(TenantId.Implicit, TestPrincipal))
        {
            var written = await writer.WriteAsync(
                new AuditAction("test.detection"), null, AuditOutcome.Denied, AuditClass.Required, CancellationToken.None);
            Assert.Equal(expectSuccess, written.IsSuccess);
        }
    }

    private static async Task AssertChecksAsync(IPlatformTestHost host, HealthStatus expected)
    {
        for (var check = 0; check < Checks; check++)
        {
            var report = await host.ProbeAsync(HealthCheckKind.Readiness, CancellationToken.None);
            var auditCheck = Assert.Single(report.Entries, entry => entry.Name == PlatformHealthChecks.AuditSink);
            Assert.Equal(expected, auditCheck.Status);
        }
    }
}
