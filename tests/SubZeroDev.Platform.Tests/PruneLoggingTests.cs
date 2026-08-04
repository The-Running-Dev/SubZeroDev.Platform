using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Persistence;
using SubZeroDev.Platform.Testing;

namespace SubZeroDev.Platform.Tests;

/// <summary>S6.4: pruning a poisoned row logs at warning naming it. A fresh <see cref="OutboxStore"/>
/// wraps the started host's real services with a <see cref="CapturingLogger{T}"/> in place of the
/// one dependency injection would otherwise supply, so the warning can be observed directly.</summary>
public sealed class PruneLoggingTests
{
    [Fact]
    public async Task Pruning_a_poisoned_row_logs_a_warning_naming_it()
    {
        await using var host = await PlatformTestHost.CreateBuilder()
            .WithProvider(PersistenceProvider.Sqlite)
            .WithSetting("Outbox:ProcessedRetention", "00:30:00")
            .WithSetting("Outbox:PoisonedRetention", "01:00:00")
            .StartAsync(CancellationToken.None);
        Assert.True((await host.Services.GetRequiredService<IMigrationRunner>().ApplyAsync(CancellationToken.None)).IsSuccess);

        var capability = host.Services.GetRequiredService<IProviderCapability>();
        var ambient = host.Services.GetRequiredService<IAmbientTransactionAccessor>();
        var unitOfWork = host.Services.GetRequiredService<IUnitOfWork>();
        var options = host.Services.GetRequiredService<PlatformOptions>();
        var logger = new CapturingLogger<OutboxStore>();
        var store = new OutboxStore(ambient, capability, host.Clock, options, logger);

        var id = OutboxMessageId.Create(host.Clock.UtcNow);
        var poisonedAt = host.Clock.UtcNow - TimeSpan.FromHours(2);

        var inserted = await unitOfWork.ExecuteAsync(
            TransactionIntent.Write,
            async token =>
            {
                var current = ambient.Current!;
                await using var insert = current.Connection.CreateCommand();
                insert.Transaction = current.Transaction;
                insert.CommandText = """
                    INSERT INTO platform_outbox
                        (id, sequence, occurred_at, type, payload, tenant, trace_parent, trace_state,
                         correlation, culture, attempts, next_attempt_at, first_deferred_at, claimed_by,
                         claimed_at, processed_at, poisoned_at, last_error)
                    VALUES
                        (@id, (SELECT COALESCE(MAX(sequence), 0) + 1 FROM platform_outbox), @occurredAt, @type,
                         @payload, @tenant, @traceParent, NULL, @correlation, @culture, 0, NULL, NULL,
                         NULL, NULL, NULL, @poisonedAt, @lastError);
                    """;
                AddParameter(insert, "@id", capability.EncodeIdentifier(id.Value));
                AddParameter(insert, "@occurredAt", capability.FormatInstant(host.Clock.UtcNow));
                AddParameter(insert, "@type", "test.prune");
                AddParameter(insert, "@payload", "{}");
                AddParameter(insert, "@tenant", TenantId.Implicit.ToString());
                AddParameter(insert, "@traceParent", "00-1111111111111111111111111111aaaa-2222222222222222-01");
                AddParameter(insert, "@correlation", "3333333333333333333333333333bbbb");
                AddParameter(insert, "@culture", string.Empty);
                AddParameter(insert, "@poisonedAt", capability.FormatInstant(poisonedAt));
                AddParameter(insert, "@lastError", "boom");
                await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            },
            CancellationToken.None);
        Assert.True(inserted.IsSuccess);

        var pruned = await store.PruneAsync(
            PruneTarget.PoisonedOutboxRows, host.Clock.UtcNow - TimeSpan.FromHours(1), 500, CancellationToken.None);

        Assert.True(pruned.IsSuccess);
        Assert.Equal(1, pruned.Value);
        Assert.Contains(
            logger.Entries,
            entry => entry.Level == LogLevel.Warning && entry.Message.Contains(id.ToString(), StringComparison.Ordinal));
    }

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
