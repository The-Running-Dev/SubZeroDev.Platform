using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Persistence;
using SubZeroDev.Platform.Testing;

namespace SubZeroDev.Platform.Tests;

/// <summary>S3: the audit contract, the writer and the default log sink.</summary>
public sealed class AuditTests
{
    private static readonly Principal TestPrincipal = new(
        new PrincipalId("test-issuer", "test-subject"), PrincipalKind.Account, "Test", null);

    private static readonly TenantId TestTenant = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));

    [Fact]
    public async Task S3_1_Every_field_comes_from_the_ambient_scope_not_from_a_parameter()
    {
        await using var host = await PlatformTestHost.CreateBuilder()
            .WithServices(services => services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IAuditSink>(new RecordingAuditSink())))
            .StartAsync(CancellationToken.None);

        var sink = (RecordingAuditSink)host.Services.GetServices<IAuditSink>().Single(s => s is RecordingAuditSink);
        var scopeFactory = host.Services.GetRequiredService<IOperationScopeFactory>();
        var writer = host.Services.GetRequiredService<IAuditWriter>();

        using var scope = scopeFactory.Begin(TestTenant, TestPrincipal);

        var written = await writer.WriteAsync(
            new AuditAction("test.action"), null, AuditOutcome.Allowed, AuditClass.Recorded, CancellationToken.None);

        Assert.True(written.IsSuccess);
        var recorded = Assert.Single(sink.Received);
        Assert.Equal(TestPrincipal.Id, recorded.Actor);
        Assert.Equal(TestPrincipal.Kind, recorded.ActorKind);
        Assert.Equal(TestTenant, recorded.Tenant);
        Assert.Equal(scope.Correlation, recorded.Correlation);

        // The declared surface itself carries no actor, tenant or correlation parameter.
        var parameters = typeof(IAuditWriter).GetMethod(nameof(IAuditWriter.WriteAsync))!.GetParameters();
        Assert.DoesNotContain(parameters, p => p.ParameterType == typeof(PrincipalId));
        Assert.DoesNotContain(parameters, p => p.ParameterType == typeof(TenantId));
        Assert.DoesNotContain(parameters, p => p.ParameterType == typeof(CorrelationId));
    }

    [Fact]
    public void S3_2_AuditEvent_declares_exactly_the_contracts_fixed_member_set()
    {
        var expected = new[] { "Id", "OccurredAt", "Actor", "ActorKind", "Tenant", "Action", "Resource", "Outcome", "Correlation", "Class" };

        var actual = typeof(AuditEvent).GetProperties().Select(p => p.Name).ToArray();

        Assert.Equal(expected.OrderBy(n => n, StringComparer.Ordinal), actual.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("Bearer eyJhbGciOiJIUzI1NiJ9.sub.sig")]
    [InlineData("ghp_1234567890abcdef1234567890abcdef1234")]
    [InlineData("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U")]
    public async Task S3_3_A_representative_secret_never_reaches_the_dispatched_record(string secret)
    {
        await using var host = await PlatformTestHost.CreateBuilder()
            .WithServices(services => services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IAuditSink>(new RecordingAuditSink())))
            .StartAsync(CancellationToken.None);

        var sink = (RecordingAuditSink)host.Services.GetServices<IAuditSink>().Single(s => s is RecordingAuditSink);
        var scopeFactory = host.Services.GetRequiredService<IOperationScopeFactory>();
        var writer = host.Services.GetRequiredService<IAuditWriter>();

        using var actionScope = scopeFactory.Begin(TestTenant, TestPrincipal);
        await writer.WriteAsync(
            new AuditAction(secret), null, AuditOutcome.Allowed, AuditClass.Recorded, CancellationToken.None);

        using var resourceScope = scopeFactory.Begin(TestTenant, TestPrincipal);
        await writer.WriteAsync(
            new AuditAction("test.action"), new ResourceRef(secret, "id"), AuditOutcome.Allowed, AuditClass.Recorded,
            CancellationToken.None);

        using var idScope = scopeFactory.Begin(TestTenant, TestPrincipal);
        await writer.WriteAsync(
            new AuditAction("test.action"), new ResourceRef("type", secret), AuditOutcome.Allowed, AuditClass.Recorded,
            CancellationToken.None);

        Assert.Equal(3, sink.Received.Count);
        Assert.All(sink.Received, recorded =>
        {
            Assert.DoesNotContain(secret, recorded.Action.Value, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, recorded.Resource?.Type ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, recorded.Resource?.Id ?? string.Empty, StringComparison.Ordinal);
        });

        Assert.Equal(Redaction.RedactedValue, sink.Received[0].Action.Value);
        Assert.Equal(Redaction.RedactedValue, sink.Received[1].Resource!.Value.Type);
        Assert.Equal(Redaction.RedactedValue, sink.Received[2].Resource!.Value.Id);
    }

    [Fact]
    public void S3_4_The_redaction_boundary_is_public_in_Core_and_absent_from_Observability()
    {
        Assert.True(typeof(Redaction).IsPublic);
        Assert.True(typeof(Redaction) is { IsAbstract: true, IsSealed: true });
        Assert.Equal("SubZeroDev.Platform.Core", typeof(Redaction).Namespace);

        var observabilityAssembly = typeof(SubZeroDev.Platform.Observability.RedactingActivityProcessor).Assembly;
        Assert.DoesNotContain(observabilityAssembly.GetTypes(), type => type.Name == "Redaction");
    }

    [Fact]
    public async Task S3_5_With_only_the_default_sink_a_record_reaches_the_log_and_it_is_not_durable()
    {
        var messages = new List<string>();
        var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(new CapturingLoggerProvider(messages)));
        var sink = new LogAuditSink(loggerFactory.CreateLogger<LogAuditSink>());

        Assert.False(sink.IsDurable);

        var auditEvent = new AuditEvent(
            AuditEventId.CreateNew(), DateTimeOffset.UtcNow, TestPrincipal.Id, TestPrincipal.Kind, TestTenant,
            new AuditAction("test.action"), null, AuditOutcome.Allowed, new CorrelationId(new string('a', 32)),
            AuditClass.Recorded);

        var written = await sink.WriteAsync(auditEvent, CancellationToken.None);

        Assert.True(written.IsSuccess);
        Assert.Contains(messages, message => message.Contains(auditEvent.Id.ToString(), StringComparison.Ordinal));

        // The Local profile is where "no audit package installed" is the whole composition: an
        // Operated host is required to register a durable sink (I-C2, S8), so asking it what it
        // falls back to would be asking about a shape that no longer starts.
        await using var host = await PlatformTestHost.CreateBuilder()
            .WithSetting("CompositionProfile", nameof(CompositionProfile.Local))
            .StartAsync(CancellationToken.None);

        var registry = host.Services.GetRequiredService<IAuditSinkRegistry>();
        var only = Assert.Single(registry.Registered);
        Assert.False(only.IsDurable);
    }

    [Fact]
    public async Task S3_6_A_successful_write_transaction_dispatches_on_commit_and_not_on_rollback()
    {
        var sink = new RecordingAuditSink();
        await using var host = await PlatformTestHost.CreateBuilder()
            .WithProvider(PersistenceProvider.Sqlite)
            .WithServices(services => services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditSink>(sink)))
            .StartAsync(CancellationToken.None);

        var scopeFactory = host.Services.GetRequiredService<IOperationScopeFactory>();
        var writer = host.Services.GetRequiredService<IAuditWriter>();
        var unitOfWork = host.Services.GetRequiredService<IUnitOfWork>();

        using (scopeFactory.Begin(TestTenant, TestPrincipal))
        {
            var committed = await unitOfWork.ExecuteAsync(
                TransactionIntent.Write,
                async token =>
                {
                    // Nothing dispatched yet: the write is still in flight.
                    Assert.Empty(sink.Received);
                    await writer.WriteAsync(
                        new AuditAction("test.committed"), null, AuditOutcome.Allowed, AuditClass.Required, token);
                    Assert.Empty(sink.Received);
                },
                CancellationToken.None);

            Assert.True(committed.IsSuccess);
        }

        var recorded = Assert.Single(sink.Received);
        Assert.Equal("test.committed", recorded.Action.Value);
    }

    [Fact]
    public async Task S3_6_Rolling_the_action_back_leaves_no_dispatched_audit_row()
    {
        var sink = new RecordingAuditSink();
        await using var host = await PlatformTestHost.CreateBuilder()
            .WithProvider(PersistenceProvider.Sqlite)
            .WithServices(services => services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditSink>(sink)))
            .StartAsync(CancellationToken.None);

        var scopeFactory = host.Services.GetRequiredService<IOperationScopeFactory>();
        var writer = host.Services.GetRequiredService<IAuditWriter>();
        var unitOfWork = host.Services.GetRequiredService<IUnitOfWork>();

        using (scopeFactory.Begin(TestTenant, TestPrincipal))
        {
            var result = await unitOfWork.ExecuteAsync(
                TransactionIntent.Write,
                async token =>
                {
                    await writer.WriteAsync(
                        new AuditAction("test.rolled-back"), null, AuditOutcome.Allowed, AuditClass.Required, token);
                    throw new InvalidOperationException("forces rollback");
                },
                CancellationToken.None);

            Assert.False(result.IsSuccess);
        }

        Assert.Empty(sink.Received);
    }

    [Fact]
    public async Task S3_7_A_denial_dispatches_immediately_and_survives_the_actions_own_rollback()
    {
        var sink = new RecordingAuditSink();
        await using var host = await PlatformTestHost.CreateBuilder()
            .WithProvider(PersistenceProvider.Sqlite)
            .WithServices(services => services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditSink>(sink)))
            .StartAsync(CancellationToken.None);

        var scopeFactory = host.Services.GetRequiredService<IOperationScopeFactory>();
        var writer = host.Services.GetRequiredService<IAuditWriter>();
        var unitOfWork = host.Services.GetRequiredService<IUnitOfWork>();

        using (scopeFactory.Begin(TestTenant, TestPrincipal))
        {
            var result = await unitOfWork.ExecuteAsync(
                TransactionIntent.Write,
                async token =>
                {
                    var written = await writer.WriteAsync(
                        new AuditAction("test.denied"), null, AuditOutcome.Denied, AuditClass.Required, token);

                    // Dispatched immediately — not deferred to commit — because a denial wrote no state.
                    Assert.True(written.IsSuccess);
                    Assert.Single(sink.Received);

                    throw new InvalidOperationException("forces rollback of the (nonexistent) state write");
                },
                CancellationToken.None);

            Assert.False(result.IsSuccess);
        }

        var recorded = Assert.Single(sink.Received);
        Assert.Equal("test.denied", recorded.Action.Value);
        Assert.Equal(AuditOutcome.Denied, recorded.Outcome);
    }

    [Fact]
    public async Task S3_8_A_required_write_that_a_sink_refuses_becomes_a_retryable_failure_and_degrades_readiness()
    {
        var sink = new RecordingAuditSink();
        sink.FailNextWith(_ => Result<AuditError>.Failure(AuditError.SinkUnavailable("recording")));

        await using var host = await PlatformTestHost.CreateBuilder()
            .WithServices(services => services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditSink>(sink)))
            .StartAsync(CancellationToken.None);

        var scopeFactory = host.Services.GetRequiredService<IOperationScopeFactory>();
        var writer = host.Services.GetRequiredService<IAuditWriter>();

        using (scopeFactory.Begin(TestTenant, TestPrincipal))
        {
            var written = await writer.WriteAsync(
                new AuditAction("test.required"), null, AuditOutcome.Denied, AuditClass.Required, CancellationToken.None);

            Assert.False(written.IsSuccess);
            Assert.Equal("SinkUnavailable", written.Error.Code);
            Assert.True(written.Error.IsRetryable);
        }

        var report = await host.ProbeAsync(HealthCheckKind.Readiness, CancellationToken.None);
        var auditCheck = Assert.Single(report.Entries, entry => entry.Name == PlatformHealthChecks.AuditSink);
        Assert.Equal(HealthStatus.Degraded, auditCheck.Status);
    }

    [Fact]
    public async Task S3_8_A_recorded_write_that_a_sink_refuses_leaves_the_response_unaffected_but_still_degrades()
    {
        var sink = new RecordingAuditSink();
        sink.FailNextWith(_ => Result<AuditError>.Failure(AuditError.SinkUnavailable("recording")));

        await using var host = await PlatformTestHost.CreateBuilder()
            .WithServices(services => services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditSink>(sink)))
            .StartAsync(CancellationToken.None);

        var scopeFactory = host.Services.GetRequiredService<IOperationScopeFactory>();
        var writer = host.Services.GetRequiredService<IAuditWriter>();

        using (scopeFactory.Begin(TestTenant, TestPrincipal))
        {
            var written = await writer.WriteAsync(
                new AuditAction("test.recorded"), null, AuditOutcome.Denied, AuditClass.Recorded, CancellationToken.None);

            Assert.True(written.IsSuccess);
        }

        var report = await host.ProbeAsync(HealthCheckKind.Readiness, CancellationToken.None);
        var auditCheck = Assert.Single(report.Entries, entry => entry.Name == PlatformHealthChecks.AuditSink);
        Assert.Equal(HealthStatus.Degraded, auditCheck.Status);
    }

    [Fact]
    public void S3_9_Two_sinks_sharing_a_name_are_rejected_naming_both()
    {
        var registry = new AuditSinkRegistry();

        Assert.True(registry.Register(new RecordingAuditSink("shared")).IsSuccess);
        var second = registry.Register(new RecordingAuditSink("shared"));

        Assert.False(second.IsSuccess);
        Assert.Equal("DuplicateProviderName", second.Error.Code);
        Assert.Contains("shared", second.Error.Detail, StringComparison.Ordinal);
        Assert.Contains(nameof(RecordingAuditSink), second.Error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task S3_9_Two_sinks_sharing_a_name_fail_startup()
    {
        var thrown = await Assert.ThrowsAsync<Hosting.PlatformStartupException>(() => PlatformTestHost.CreateBuilder()
            .WithServices(services =>
            {
                services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditSink>(new RecordingAuditSink("dup-one")));
                services.AddSingleton<IAuditSink>(new RecordingAuditSink("dup-one"));
            })
            .StartAsync(CancellationToken.None));

        var error = Assert.IsType<Hosting.HostStartupError>(thrown.Error);
        Assert.Equal("Registration", error.Code);
        Assert.Equal("DuplicateProviderName", error.Inner?.Code);
    }

    private sealed class CapturingLoggerProvider(List<string> messages) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(messages);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(List<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                messages.Add(formatter(state, exception));

            private sealed class NullScope : IDisposable
            {
                internal static readonly NullScope Instance = new();

                public void Dispose()
                {
                }
            }
        }
    }
}
