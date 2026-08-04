using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Data.Sqlite;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Persistence;
using SubZeroDev.Platform.Testing;

namespace SubZeroDev.Platform.Tests;

public sealed class OutboxDispatchTests
{
    [Fact]
    public async Task One_tick_dispatches_every_due_message_up_to_its_budget()
    {
        CountingTestEventHandler.Count = 0;
        await using var host = await StartWorkerAsync();
        await MigrateAndEnqueueAsync(host, 3);

        await host.RunBackgroundWorkOnceAsync(PlatformBackgroundWork.OutboxDispatch, CancellationToken.None);

        Assert.Equal(3, CountingTestEventHandler.Count);
        Assert.Equal(3, host.Events.Dispatched.Count);
        host.Events.Clear();
        Assert.Empty(host.Events.Enqueued);
        Assert.Empty(host.Events.Dispatched);
    }

    [Fact]
    public async Task One_tick_stops_after_the_configured_budget()
    {
        CountingTestEventHandler.Count = 0;
        await using var host = await StartWorkerAsync(dispatchTickBudget: 2);
        await MigrateAndEnqueueAsync(host, 5);

        await host.RunBackgroundWorkOnceAsync(PlatformBackgroundWork.OutboxDispatch, CancellationToken.None);

        Assert.Equal(2, CountingTestEventHandler.Count);
    }

    [Fact]
    public async Task Transient_failure_waits_for_its_backoff_and_an_escaping_exception_uses_the_same_path()
    {
        await using var fixture = await ScriptedFixture.StartAsync();
        fixture.Script.ExceptionsRemaining = 1;
        var id = await MigrateAndEnqueueOneAsync(fixture.Host);

        await TickAsync(fixture.Host);
        var failed = await fixture.ReadAsync(id);
        Assert.Equal(1, failed.Attempts);
        Assert.Equal(fixture.Host.Clock.UtcNow + TimeSpan.FromSeconds(30), failed.NextAttemptAt);
        Assert.Null(failed.PoisonedAt);

        fixture.Host.Clock.Advance(TimeSpan.FromSeconds(29));
        await TickAsync(fixture.Host);
        Assert.Single(fixture.Script.Observed);

        fixture.Host.Clock.Advance(TimeSpan.FromSeconds(1));
        await TickAsync(fixture.Host);
        Assert.Equal(2, fixture.Script.Observed.Count);
        Assert.NotNull((await fixture.ReadAsync(id)).ProcessedAt);
    }

    [Fact]
    public async Task Permanent_failure_poisons_on_attempt_one_and_dispatch_failures_preserve_attempts()
    {
        await using var fixture = await ScriptedFixture.StartAsync();
        fixture.Script.Results.Enqueue(Result<HandlerError>.Failure(HandlerError.Permanent()));
        var permanentId = await MigrateAndEnqueueOneAsync(fixture.Host);

        await TickAsync(fixture.Host);
        var permanent = await fixture.ReadAsync(permanentId);
        Assert.Equal(1, permanent.Attempts);
        Assert.NotNull(permanent.PoisonedAt);
        Assert.NotNull(permanent.LastError);

        var unresolvedId = await EnqueueOneAsync(fixture.Host);
        await fixture.ExecuteAsync("UPDATE platform_outbox SET type = 'missing.event' WHERE id = @id;", unresolvedId);
        await TickAsync(fixture.Host);
        var firstDeferral = await fixture.ReadAsync(unresolvedId);
        Assert.Equal(0, firstDeferral.Attempts);
        Assert.NotNull(firstDeferral.FirstDeferredAt);
        Assert.Equal(fixture.Host.Clock.UtcNow + TimeSpan.FromMinutes(1), firstDeferral.NextAttemptAt);

        fixture.Host.Clock.Advance(TimeSpan.FromMinutes(1));
        await TickAsync(fixture.Host);
        var secondDeferral = await fixture.ReadAsync(unresolvedId);
        Assert.Equal(firstDeferral.FirstDeferredAt, secondDeferral.FirstDeferredAt);

        fixture.Host.Clock.Advance(TimeSpan.FromHours(23) + TimeSpan.FromMinutes(59));
        await TickAsync(fixture.Host);
        var agedOut = await fixture.ReadAsync(unresolvedId);
        Assert.Equal(0, agedOut.Attempts);
        Assert.NotNull(agedOut.PoisonedAt);
    }

    [Fact]
    public async Task Twelfth_transient_failure_poisons_after_non_decreasing_backoff_capped_at_six_hours()
    {
        await using var fixture = await ScriptedFixture.StartAsync();
        for (var attempt = 0; attempt < 12; attempt++)
        {
            fixture.Script.Results.Enqueue(Result<HandlerError>.Failure(HandlerError.Transient()));
        }

        var id = await MigrateAndEnqueueOneAsync(fixture.Host);
        var previousDelay = TimeSpan.Zero;
        for (var attempt = 1; attempt <= 12; attempt++)
        {
            var at = fixture.Host.Clock.UtcNow;
            await TickAsync(fixture.Host);
            var row = await fixture.ReadAsync(id);
            Assert.Equal(attempt, row.Attempts);

            if (attempt == 12)
            {
                Assert.NotNull(row.PoisonedAt);
                var observed = fixture.Script.Observed.Count;
                await TickAsync(fixture.Host);
                Assert.Equal(observed, fixture.Script.Observed.Count);
                break;
            }

            var delay = row.NextAttemptAt!.Value - at;
            Assert.True(delay >= previousDelay);
            Assert.True(delay <= TimeSpan.FromHours(6));
            previousDelay = delay;
            fixture.Host.Clock.Advance(delay);
        }
    }

    [Fact]
    public async Task Invalid_payload_defers_without_consuming_an_attempt()
    {
        await using var fixture = await ScriptedFixture.StartAsync();
        var invalidId = await MigrateAndEnqueueOneAsync(fixture.Host);
        await fixture.ExecuteAsync("UPDATE platform_outbox SET payload = '{' WHERE id = @id;", invalidId);

        await TickAsync(fixture.Host);
        var invalid = await fixture.ReadAsync(invalidId);
        Assert.Equal(0, invalid.Attempts);
        Assert.NotNull(invalid.FirstDeferredAt);
        Assert.Null(invalid.ClaimedBy);
    }

    [Fact]
    public async Task Pending_migrations_prevent_claiming_or_stamping_the_row()
    {
        await using var fixture = await ScriptedFixture.StartAsync(services =>
            services.AddSingleton<IMigrationRunner, PendingMigrationRunner>());
        var realRunner = new MigrationRunner(
            fixture.Host.Services.GetServices<IModuleMigrationSource>(),
            fixture.Host.Services.GetRequiredService<IProviderCapability>(),
            fixture.Host.Clock);
        Assert.True((await realRunner.ApplyAsync(CancellationToken.None)).IsSuccess);
        var id = await EnqueueOneAsync(fixture.Host);

        await TickAsync(fixture.Host);

        var row = await fixture.ReadAsync(id);
        Assert.Equal(0, row.Attempts);
        Assert.Null(row.ClaimedBy);
        Assert.Null(row.FirstDeferredAt);
    }

    [Fact]
    public async Task Follow_up_events_keep_the_origin_correlation_and_store_each_linked_trace()
    {
        var path = Path.Combine(Path.GetTempPath(), $"platform-chain-{Guid.NewGuid():N}.db");
        var script = new DispatchScript();
        var host = await PlatformTestHost.CreateBuilder()
            .WithRole(HostRole.Worker)
            .WithProvider(PersistenceProvider.Sqlite)
            .WithSetting("Persistence:ConnectionString", $"Data Source={path}")
            .WithServices(services =>
            {
                services.AddSingleton(script);
                services.AddPlatformEventHandler<ChainedTestEvent, ChainedTestEventHandler>(new EventTypeName("chain.event"));
            })
            .StartAsync(CancellationToken.None);
        try
        {
            Assert.True((await host.Services.GetRequiredService<IMigrationRunner>().ApplyAsync(CancellationToken.None)).IsSuccess);
            var origin = new TraceContext("00-1111111111111111111111111111aaaa-2222222222222222-01", null);
            var correlation = new CorrelationId("3333333333333333333333333333bbbb");
            using (host.Services.GetRequiredService<IOperationScopeFactory>().Begin(
                origin, correlation, TenantId.Implicit, null, new CultureTag("bg")))
            {
                var writer = host.Services.GetRequiredService<IOutboxWriter>();
                var committed = await host.Services.GetRequiredService<IUnitOfWork>().ExecuteAsync(
                    TransactionIntent.Write,
                    token =>
                    {
                        writer.Enqueue(new ChainedTestEvent(3));
                        return Task.CompletedTask;
                    },
                    CancellationToken.None);
                Assert.True(committed.IsSuccess);
            }

            await TickAsync(host);
            Assert.Equal(4, script.Observed.Count);
            Assert.All(script.Observed, observed => Assert.Equal(correlation, observed.Correlation));
            Assert.Equal(4, host.Events.Dispatched.Count);

            await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT correlation, trace_parent FROM platform_outbox ORDER BY sequence;";
            await using var reader = await command.ExecuteReaderAsync();
            var traces = new List<string>();
            while (await reader.ReadAsync())
            {
                Assert.Equal(correlation.TraceId, reader.GetString(0));
                traces.Add(reader.GetString(1));
            }

            Assert.Equal(4, traces.Count);
            Assert.Equal(origin.TraceParent, traces[0]);
            Assert.Equal(4, traces.Distinct(StringComparer.Ordinal).Count());
        }
        finally
        {
            await host.DisposeAsync();
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                try { File.Delete(path + suffix); } catch (IOException) { }
            }
        }
    }

    [Fact]
    public async Task Handler_observes_the_stored_context_on_a_new_linked_trace()
    {
        await using var fixture = await ScriptedFixture.StartAsync();
        Assert.True((await fixture.Host.Services.GetRequiredService<IMigrationRunner>().ApplyAsync(CancellationToken.None)).IsSuccess);
        var origin = new TraceContext("00-1111111111111111111111111111aaaa-2222222222222222-01", "vendor=value");
        var correlation = new CorrelationId("3333333333333333333333333333bbbb");
        var scopes = fixture.Host.Services.GetRequiredService<IOperationScopeFactory>();
        using (scopes.Begin(origin, correlation, TenantId.Implicit, null, new CultureTag("bg")))
        {
            await EnqueueOneAsync(fixture.Host);
        }

        await TickAsync(fixture.Host);
        var observed = Assert.Single(fixture.Script.Observed);
        Assert.Equal(correlation, observed.Correlation);
        Assert.Equal(TenantId.Implicit, observed.Tenant);
        Assert.Equal(new CultureTag("bg"), observed.Culture);
        Assert.NotEqual(origin.TraceId, observed.Trace.TraceId);
        Assert.True(observed.Trace.Sampled);
        Assert.Null(observed.Principal);
    }

    [Fact]
    public async Task Shutdown_drains_an_in_flight_handler_that_finishes_inside_the_window()
    {
        await using var fixture = await ScriptedFixture.StartAsync();
        fixture.Script.Block = true;
        var id = await MigrateAndEnqueueOneAsync(fixture.Host);
        using var shutdown = new CancellationTokenSource();

        var tick = fixture.Host.RunBackgroundWorkOnceAsync(PlatformBackgroundWork.OutboxDispatch, shutdown.Token);
        await fixture.Script.Started.Task;
        await shutdown.CancelAsync();
        fixture.Script.Release.TrySetResult();
        await tick;

        Assert.NotNull((await fixture.ReadAsync(id)).ProcessedAt);
    }

    [Fact]
    public async Task Shutdown_after_claim_releases_the_message_before_its_handler_starts()
    {
        await using var fixture = await ScriptedFixture.StartAsync();
        var id = await MigrateAndEnqueueOneAsync(fixture.Host);
        using var shutdown = new CancellationTokenSource();
        var services = fixture.Host.Services;
        var dispatcher = new OutboxDispatcher(
            new CancelAfterClaimOutboxStore(services.GetRequiredService<IOutboxStore>(), shutdown),
            services.GetRequiredService<IEventHandlerRegistry>(),
            services.GetRequiredService<IMigrationRunner>(),
            services.GetRequiredService<IServiceScopeFactory>(),
            services.GetRequiredService<IOperationScopeFactory>(),
            services.GetRequiredService<ITraceContextCodec>(),
            services.GetRequiredService<PlatformOptions>(),
            services.GetRequiredService<InstanceId>(),
            services.GetRequiredService<IClock>(),
            services.GetRequiredService<ILogger<OutboxDispatcher>>());

        await dispatcher.TickAsync(shutdown.Token);

        Assert.Null((await fixture.ReadAsync(id)).ClaimedBy);
        Assert.Empty(fixture.Script.Observed);
    }

    [Fact]
    public async Task Shutdown_abandons_a_handler_past_the_drain_window_and_leaves_its_claim()
    {
        await using var fixture = await ScriptedFixture.StartAsync(
            configure: null,
            drainWindow: TimeSpan.FromMilliseconds(100));
        fixture.Script.Block = true;
        var id = await MigrateAndEnqueueOneAsync(fixture.Host);
        using var shutdown = new CancellationTokenSource();

        var tick = fixture.Host.RunBackgroundWorkOnceAsync(PlatformBackgroundWork.OutboxDispatch, shutdown.Token);
        await fixture.Script.Started.Task;
        await shutdown.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tick);

        var row = await fixture.ReadAsync(id);
        Assert.NotNull(row.ClaimedBy);
        Assert.Null(row.ProcessedAt);
    }

    private static async Task<IPlatformTestHost> StartWorkerAsync(int? dispatchTickBudget = null)
    {
        var builder = PlatformTestHost.CreateBuilder()
            .WithRole(HostRole.Worker)
            .WithProvider(PersistenceProvider.Sqlite)
            .WithServices(services => services.AddPlatformEventHandler<TestEvent, CountingTestEventHandler>(new EventTypeName("test.event")));

        if (dispatchTickBudget is { } budget)
        {
            builder.WithSetting("Outbox:DispatchTickBudget", budget.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return await builder.StartAsync(CancellationToken.None);
    }

    private static async Task MigrateAndEnqueueAsync(IPlatformTestHost host, int count)
    {
        var migrations = host.Services.GetRequiredService<IMigrationRunner>();
        Assert.True((await migrations.ApplyAsync(CancellationToken.None)).IsSuccess);

        var scopeFactory = host.Services.GetRequiredService<IOperationScopeFactory>();
        var unitOfWork = host.Services.GetRequiredService<IUnitOfWork>();
        var writer = host.Services.GetRequiredService<IOutboxWriter>();

        for (var index = 0; index < count; index++)
        {
            using var scope = scopeFactory.Begin(TenantId.Implicit, null);
            var enqueued = await unitOfWork.ExecuteAsync(
                TransactionIntent.Write,
                _ =>
                {
                    writer.Enqueue(new TestEvent($"event-{index}"));
                    return Task.CompletedTask;
                },
                CancellationToken.None);
            Assert.True(enqueued.IsSuccess);
        }
    }

    private static Task TickAsync(IPlatformTestHost host) =>
        host.RunBackgroundWorkOnceAsync(PlatformBackgroundWork.OutboxDispatch, CancellationToken.None);

    private static async Task<OutboxMessageId> MigrateAndEnqueueOneAsync(IPlatformTestHost host)
    {
        Assert.True((await host.Services.GetRequiredService<IMigrationRunner>().ApplyAsync(CancellationToken.None)).IsSuccess);
        return await EnqueueOneAsync(host);
    }

    private static async Task<OutboxMessageId> EnqueueOneAsync(IPlatformTestHost host)
    {
        var writer = host.Services.GetRequiredService<IOutboxWriter>();
        var unitOfWork = host.Services.GetRequiredService<IUnitOfWork>();
        var scopes = host.Services.GetRequiredService<IOperationScopeFactory>();
        var id = default(OutboxMessageId);
        if (host.Services.GetRequiredService<IOperationScopeAccessor>().Current is null)
        {
            using var scope = scopes.Begin(TenantId.Implicit, null);
            await CommitAsync();
        }
        else
        {
            await CommitAsync();
        }

        return id;

        async Task CommitAsync()
        {
            var committed = await unitOfWork.ExecuteAsync(
                TransactionIntent.Write,
                token =>
                {
                    id = writer.Enqueue(new TestEvent());
                    return Task.CompletedTask;
                },
                CancellationToken.None);
            Assert.True(committed.IsSuccess);
        }
    }

    private sealed class ScriptedFixture(IPlatformTestHost host, DispatchScript script, string path) : IAsyncDisposable
    {
        internal IPlatformTestHost Host { get; } = host;
        internal DispatchScript Script { get; } = script;

        internal static async Task<ScriptedFixture> StartAsync(
            Action<IServiceCollection>? configure = null,
            TimeSpan? drainWindow = null)
        {
            var path = Path.Combine(Path.GetTempPath(), $"platform-dispatch-{Guid.NewGuid():N}.db");
            var script = new DispatchScript();
            var builder = PlatformTestHost.CreateBuilder()
                .WithRole(HostRole.Worker)
                .WithProvider(PersistenceProvider.Sqlite)
                .WithSetting("Persistence:ConnectionString", $"Data Source={path}")
                .WithServices(services =>
                {
                    services.AddSingleton(script);
                    configure?.Invoke(services);
                    services.AddPlatformEventHandler<TestEvent, ScriptedTestEventHandler>(new EventTypeName("test.event"));
                });
            if (drainWindow is { } window)
            {
                builder.WithSetting("Hosting:GracefulShutdownDrainWindow", window.ToString("c", System.Globalization.CultureInfo.InvariantCulture));
            }

            var host = await builder.StartAsync(CancellationToken.None);
            return new ScriptedFixture(host, script, path);
        }

        internal async Task<RowState> ReadAsync(OutboxMessageId id)
        {
            await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT attempts, next_attempt_at, first_deferred_at, claimed_by, processed_at, poisoned_at, last_error FROM platform_outbox WHERE id = @id;";
            command.Parameters.AddWithValue("@id", Host.Services.GetRequiredService<IProviderCapability>().EncodeIdentifier(id.Value));
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            DateTimeOffset? Instant(int ordinal) => reader.IsDBNull(ordinal)
                ? null
                : Host.Services.GetRequiredService<IProviderCapability>().TryParseInstant(reader.GetString(ordinal), out var value)
                    ? value
                    : throw new InvalidOperationException("Invalid test instant.");
            return new RowState(
                reader.GetInt32(0), Instant(1), Instant(2), reader.IsDBNull(3) ? null : reader.GetString(3),
                Instant(4), Instant(5), reader.IsDBNull(6) ? null : reader.GetString(6));
        }

        internal async Task ExecuteAsync(string sql, OutboxMessageId id)
        {
            await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("@id", Host.Services.GetRequiredService<IProviderCapability>().EncodeIdentifier(id.Value));
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                try { File.Delete(path + suffix); } catch (IOException) { }
            }
        }
    }

    private sealed record RowState(
        int Attempts,
        DateTimeOffset? NextAttemptAt,
        DateTimeOffset? FirstDeferredAt,
        string? ClaimedBy,
        DateTimeOffset? ProcessedAt,
        DateTimeOffset? PoisonedAt,
        string? LastError);

    private sealed class CancelAfterClaimOutboxStore(IOutboxStore inner, CancellationTokenSource shutdown)
        : IOutboxStore
    {
        public Task<Result<TransactionError>> InsertAsync(
            OutboxMessage message, CancellationToken cancellationToken) =>
            inner.InsertAsync(message, cancellationToken);

        public async Task<Result<OutboxMessage?, TransactionError>> ClaimNextAsync(
            InstanceId holder, CancellationToken cancellationToken)
        {
            var claimed = await inner.ClaimNextAsync(holder, cancellationToken);
            await shutdown.CancelAsync();
            return claimed;
        }

        public Task<Result<ClaimedWriteOutcome, TransactionError>> MarkProcessedAsync(
            OutboxMessageId id, InstanceId holder, CancellationToken cancellationToken) =>
            inner.MarkProcessedAsync(id, holder, cancellationToken);

        public Task<Result<ClaimedWriteOutcome, TransactionError>> RecordFailureAsync(
            OutboxMessageId id, InstanceId holder, string error, DateTimeOffset nextAttemptAt,
            CancellationToken cancellationToken) =>
            inner.RecordFailureAsync(id, holder, error, nextAttemptAt, cancellationToken);

        public Task<Result<ClaimedWriteOutcome, TransactionError>> PoisonAsync(
            OutboxMessageId id, InstanceId holder, string error, PoisonAttemptMode attemptMode,
            CancellationToken cancellationToken) =>
            inner.PoisonAsync(id, holder, error, attemptMode, cancellationToken);

        public Task<Result<ClaimedWriteOutcome, TransactionError>> DeferAsync(
            OutboxMessageId id, InstanceId holder, DateTimeOffset nextAttemptAt,
            CancellationToken cancellationToken) =>
            inner.DeferAsync(id, holder, nextAttemptAt, cancellationToken);

        public Task<Result<ClaimedWriteOutcome, TransactionError>> ReleaseClaimAsync(
            OutboxMessageId id, InstanceId holder, CancellationToken cancellationToken) =>
            inner.ReleaseClaimAsync(id, holder, cancellationToken);

        public Task<Result<DateTimeOffset?, TransactionError>> OldestPendingDueAsync(CancellationToken cancellationToken) =>
            inner.OldestPendingDueAsync(cancellationToken);

        public Task<Result<long, TransactionError>> PendingCountAsync(CancellationToken cancellationToken) =>
            inner.PendingCountAsync(cancellationToken);

        public Task<Result<long, TransactionError>> PoisonedCountAsync(CancellationToken cancellationToken) =>
            inner.PoisonedCountAsync(cancellationToken);

        public Task<Result<int, TransactionError>> PruneAsync(
            PruneTarget target, DateTimeOffset olderThan, int batchSize, CancellationToken cancellationToken) =>
            inner.PruneAsync(target, olderThan, batchSize, cancellationToken);
    }
}
