using Microsoft.Extensions.DependencyInjection;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Audit;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Persistence;
using SubZeroDev.Platform.Testing;

namespace SubZeroDev.Platform.Tests;

/// <summary>D5-S13: the durable audit store — one table, an <see cref="IAuditSink"/> declaring
/// <c>IsDurable</c>, and the read API scoped by tenant, instant range, actor and correlation.</summary>
public sealed class AuditStoreTests
{
    private static readonly Principal ActorA = new(
        new PrincipalId("issuer-a", "subject-a"), PrincipalKind.Account, "A", null);

    private static readonly Principal ActorB = new(
        new PrincipalId("issuer-b", "subject-b"), PrincipalKind.Delegated, "B", null);

    private static readonly TenantId TenantOne = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly TenantId TenantTwo = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));

    // S13.1 -----------------------------------------------------------------------------------

    [Fact]
    public async Task S13_1_Allowed_denied_and_failed_actions_persist_every_named_field_and_survive_a_restart()
    {
        var database = TemporaryPath();
        try
        {
            string correlationText;

            await using (var host = await StartHostAsync(database))
            {
                var scopeFactory = host.Services.GetRequiredService<IOperationScopeFactory>();
                var writer = host.Services.GetRequiredService<IAuditWriter>();

                using var scope = scopeFactory.Begin(TenantOne, ActorA);
                correlationText = scope.Correlation.TraceId;

                Assert.True((await writer.WriteAsync(
                    new AuditAction("test.allowed"), new ResourceRef("thing", "1"),
                    AuditOutcome.Allowed, AuditClass.Recorded, CancellationToken.None)).IsSuccess);
                Assert.True((await writer.WriteAsync(
                    new AuditAction("test.denied"), null,
                    AuditOutcome.Denied, AuditClass.Required, CancellationToken.None)).IsSuccess);
                Assert.True((await writer.WriteAsync(
                    new AuditAction("test.failed"), null,
                    AuditOutcome.Failed, AuditClass.Recorded, CancellationToken.None)).IsSuccess);
            }

            // The restart: a fresh host over the same database file.
            await using var restarted = await StartHostAsync(database);
            var readApi = restarted.Services.GetRequiredService<IAuditReadApi>();
            var result = await readApi.ByTenantAsync(
                TenantOne, DateTimeOffset.MinValue, DateTimeOffset.MaxValue, CancellationToken.None);

            Assert.True(result.IsSuccess);
            Assert.Equal(3, result.Value.Count);
            Assert.All(result.Value, recorded =>
            {
                Assert.Equal(ActorA.Id, recorded.Actor);
                Assert.Equal(ActorA.Kind, recorded.ActorKind);
                Assert.Equal(TenantOne, recorded.Tenant);
                Assert.Equal(correlationText, recorded.Correlation.TraceId);
            });

            Assert.Contains(result.Value, e => e.Outcome == AuditOutcome.Allowed);
            Assert.Contains(result.Value, e => e.Outcome == AuditOutcome.Denied);
            Assert.Contains(result.Value, e => e.Outcome == AuditOutcome.Failed);
        }
        finally
        {
            DeleteSqliteFiles(database);
        }
    }

    // S13.2 -----------------------------------------------------------------------------------

    [Fact]
    public async Task S13_2_The_actor_is_stored_as_two_columns_and_round_trips_without_being_split()
    {
        var database = TemporaryPath();
        try
        {
            await using var host = await StartHostAsync(database);
            var scopeFactory = host.Services.GetRequiredService<IOperationScopeFactory>();
            var writer = host.Services.GetRequiredService<IAuditWriter>();

            using (scopeFactory.Begin(TenantOne, ActorA))
            {
                await writer.WriteAsync(
                    new AuditAction("test.action"), null, AuditOutcome.Allowed, AuditClass.Recorded,
                    CancellationToken.None);
            }

            var row = await ReadRawColumnsAsync(host, "actor_issuer", "actor_subject");
            Assert.Equal("issuer-a", row["actor_issuer"]);
            Assert.Equal("subject-a", row["actor_subject"]);

            var readApi = host.Services.GetRequiredService<IAuditReadApi>();
            var byActor = await readApi.ByActorAsync(
                ActorA.Id, DateTimeOffset.MinValue, DateTimeOffset.MaxValue, CancellationToken.None);
            Assert.True(byActor.IsSuccess);
            var recorded = Assert.Single(byActor.Value);
            Assert.Equal(ActorA.Id, recorded.Actor);
        }
        finally
        {
            DeleteSqliteFiles(database);
        }
    }

    // S13.3 -----------------------------------------------------------------------------------

    [Fact]
    public async Task S13_3_The_read_API_answers_by_tenant_by_correlation_and_by_actor()
    {
        var database = TemporaryPath();
        try
        {
            await using var host = await StartHostAsync(database);
            var scopeFactory = host.Services.GetRequiredService<IOperationScopeFactory>();
            var writer = host.Services.GetRequiredService<IAuditWriter>();

            string firstCorrelation;
            using (var scope = scopeFactory.Begin(TenantOne, ActorA))
            {
                firstCorrelation = scope.Correlation.TraceId;
                await writer.WriteAsync(
                    new AuditAction("test.one"), null, AuditOutcome.Allowed, AuditClass.Recorded,
                    CancellationToken.None);
            }

            using (scopeFactory.Begin(TenantTwo, ActorB))
            {
                await writer.WriteAsync(
                    new AuditAction("test.two"), null, AuditOutcome.Allowed, AuditClass.Recorded,
                    CancellationToken.None);
            }

            var readApi = host.Services.GetRequiredService<IAuditReadApi>();

            var byTenant = await readApi.ByTenantAsync(
                TenantOne, DateTimeOffset.MinValue, DateTimeOffset.MaxValue, CancellationToken.None);
            Assert.True(byTenant.IsSuccess);
            Assert.Equal("test.one", Assert.Single(byTenant.Value).Action.Value);

            var byCorrelation = await readApi.ByCorrelationAsync(
                new CorrelationId(firstCorrelation), CancellationToken.None);
            Assert.True(byCorrelation.IsSuccess);
            Assert.Equal("test.one", Assert.Single(byCorrelation.Value).Action.Value);

            var byActor = await readApi.ByActorAsync(
                ActorB.Id, DateTimeOffset.MinValue, DateTimeOffset.MaxValue, CancellationToken.None);
            Assert.True(byActor.IsSuccess);
            Assert.Equal("test.two", Assert.Single(byActor.Value).Action.Value);

            // Exactly the three indexes those reads need — nothing else, because every index is a
            // write cost on the path every security-sensitive action takes (S13.3).
            var indexes = await ListIndexesAsync(host);
            Assert.Equal(3, indexes.Count);
            Assert.Contains(indexes, name => name == "ix_audit_event_tenant_occurred_at");
            Assert.Contains(indexes, name => name == "ix_audit_event_correlation");
            Assert.Contains(indexes, name => name == "ix_audit_event_actor_subject_occurred_at");
        }
        finally
        {
            DeleteSqliteFiles(database);
        }
    }

    // S13.4 -----------------------------------------------------------------------------------

    [Fact]
    public void S13_4_The_module_exposes_no_update_and_no_delete()
    {
        var publicMembers = typeof(IAuditReadApi).Assembly.GetExportedTypes()
            .SelectMany(type => type.GetMethods())
            .Select(method => method.Name)
            .ToList();

        Assert.DoesNotContain(publicMembers, name =>
            name.Contains("Update", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Delete", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Remove", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Clear", StringComparison.OrdinalIgnoreCase));

        // The read API's whole surface is the three scoped reads named in the contract.
        var readMembers = typeof(IAuditReadApi).GetMethods().Select(m => m.Name).OrderBy(n => n, StringComparer.Ordinal);
        Assert.Equal(
            new[] { nameof(IAuditReadApi.ByActorAsync), nameof(IAuditReadApi.ByCorrelationAsync), nameof(IAuditReadApi.ByTenantAsync) }
                .OrderBy(n => n, StringComparer.Ordinal),
            readMembers);
    }

    // S13.5 -----------------------------------------------------------------------------------

    [Fact]
    public async Task S13_5_The_primary_key_is_the_event_id_alone_carrying_no_tenant_prefix()
    {
        var database = TemporaryPath();
        try
        {
            await using var host = await StartHostAsync(database);

            var unitOfWork = host.Services.GetRequiredService<IUnitOfWork>();
            var ambient = host.Services.GetRequiredService<IAmbientTransactionAccessor>();

            var result = await unitOfWork.ExecuteAsync(
                TransactionIntent.ReadOnly,
                async ct =>
                {
                    await using var command = ambient.Current!.Connection.CreateCommand();
                    command.Transaction = ambient.Current!.Transaction;
                    command.CommandText = "PRAGMA table_info(audit_event);";

                    var primaryKeyColumns = new List<string>();
                    await using var reader = await command.ExecuteReaderAsync(ct);
                    while (await reader.ReadAsync(ct))
                    {
                        // PRAGMA table_info: columns are cid, name, type, notnull, dflt_value, pk.
                        if (reader.GetInt32(5) > 0)
                        {
                            primaryKeyColumns.Add(reader.GetString(1));
                        }
                    }

                    return primaryKeyColumns;
                },
                CancellationToken.None);

            Assert.True(result.IsSuccess);
            Assert.Equal(["event_id"], result.Value);
        }
        finally
        {
            DeleteSqliteFiles(database);
        }
    }

    // S13.6 -----------------------------------------------------------------------------------

    [Fact]
    public async Task S13_6_The_module_registers_a_sink_declaring_IsDurable_and_the_operated_host_starts()
    {
        var database = TemporaryPath();
        try
        {
            // PlatformTestHost defaults to Operated (I-C1/I-C2 must be satisfied for the host to
            // start at all): registering only the Audit module here proves its own sink is what
            // clears I-C2, not the test harness's own fallback fake, by name.
            await using var host = await StartHostAsync(database);

            var registry = host.Services.GetRequiredService<IAuditSinkRegistry>();
            var durable = Assert.Single(registry.Registered, sink => sink.Name == "platform.audit.durable");
            Assert.True(durable.IsDurable);

            // The negative half — Operated with no durable sink at all refuses to start with
            // HostStartupError.DurableAuditSinkRequired — is S8.6, proved generically in
            // CompositionProfileTests against a fake sink; this slice's contribution is that the
            // real module satisfies the same check.
        }
        finally
        {
            DeleteSqliteFiles(database);
        }
    }

    // S13.8 -----------------------------------------------------------------------------------

    [Theory]
    [InlineData("Bearer eyJhbGciOiJIUzI1NiJ9.sub.sig")]
    [InlineData("ghp_1234567890abcdef1234567890abcdef1234")]
    public async Task S13_8_A_representative_secret_never_reaches_the_stored_row(string secret)
    {
        var database = TemporaryPath();
        try
        {
            await using var host = await StartHostAsync(database);
            var scopeFactory = host.Services.GetRequiredService<IOperationScopeFactory>();
            var writer = host.Services.GetRequiredService<IAuditWriter>();

            using (scopeFactory.Begin(TenantOne, ActorA))
            {
                await writer.WriteAsync(
                    new AuditAction(secret), new ResourceRef(secret, secret), AuditOutcome.Allowed,
                    AuditClass.Recorded, CancellationToken.None);
            }

            var readApi = host.Services.GetRequiredService<IAuditReadApi>();
            var result = await readApi.ByTenantAsync(
                TenantOne, DateTimeOffset.MinValue, DateTimeOffset.MaxValue, CancellationToken.None);

            Assert.True(result.IsSuccess);
            var recorded = Assert.Single(result.Value);
            Assert.DoesNotContain(secret, recorded.Action.Value, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, recorded.Resource?.Type ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, recorded.Resource?.Id ?? string.Empty, StringComparison.Ordinal);
            Assert.Equal(Redaction.RedactedValue, recorded.Action.Value);
        }
        finally
        {
            DeleteSqliteFiles(database);
        }
    }

    // Helpers -----------------------------------------------------------------------------------

    private static async Task<IPlatformTestHost> StartHostAsync(string databasePath)
    {
        var host = await PlatformTestHost.CreateBuilder()
            .WithProvider(PersistenceProvider.Sqlite)
            .WithSetting("Persistence:ConnectionString", $"Data Source={databasePath}")
            .WithServices(services => services.AddSingleton<IPlatformModule, AuditModule>())
            .StartAsync(CancellationToken.None);

        var migrated = await host.Services.GetRequiredService<IMigrationRunner>().ApplyAsync(CancellationToken.None);
        Assert.True(migrated.IsSuccess);
        return host;
    }

    private static async Task<Dictionary<string, string>> ReadRawColumnsAsync(IPlatformTestHost host, params string[] columns)
    {
        var unitOfWork = host.Services.GetRequiredService<IUnitOfWork>();
        var ambient = host.Services.GetRequiredService<IAmbientTransactionAccessor>();

        var result = await unitOfWork.ExecuteAsync(
            TransactionIntent.ReadOnly,
            async ct =>
            {
                await using var command = ambient.Current!.Connection.CreateCommand();
                command.Transaction = ambient.Current!.Transaction;
                command.CommandText = $"SELECT {string.Join(", ", columns)} FROM audit_event LIMIT 1;";

                var row = new Dictionary<string, string>(StringComparer.Ordinal);
                await using var reader = await command.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    for (var i = 0; i < columns.Length; i++)
                    {
                        row[columns[i]] = reader.GetString(i);
                    }
                }

                return row;
            },
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        return result.Value;
    }

    private static async Task<IReadOnlyList<string>> ListIndexesAsync(IPlatformTestHost host)
    {
        var unitOfWork = host.Services.GetRequiredService<IUnitOfWork>();
        var ambient = host.Services.GetRequiredService<IAmbientTransactionAccessor>();

        var result = await unitOfWork.ExecuteAsync(
            TransactionIntent.ReadOnly,
            async ct =>
            {
                await using var command = ambient.Current!.Connection.CreateCommand();
                command.Transaction = ambient.Current!.Transaction;
                command.CommandText =
                    "SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = 'audit_event' "
                    + "AND name NOT LIKE 'sqlite_%';";

                var names = new List<string>();
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    names.Add(reader.GetString(0));
                }

                return names;
            },
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        return result.Value;
    }

    private static string TemporaryPath() =>
        Path.Combine(Path.GetTempPath(), $"audit-store-test-{Guid.NewGuid():N}.db");

    private static void DeleteSqliteFiles(string path)
    {
        foreach (var candidate in new[] { path, path + "-wal", path + "-shm" })
        {
            try
            {
                File.Delete(candidate);
            }
            catch (IOException)
            {
                // Best-effort cleanup; a lingering handle is an orphaned temp file, not a failure.
            }
        }
    }
}
