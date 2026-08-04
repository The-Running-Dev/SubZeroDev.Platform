using Microsoft.Extensions.DependencyInjection;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Persistence;
using SubZeroDev.Platform.Testing;

namespace SubZeroDev.Platform.Tests;

/// <summary>S7: administrative recovery operates on poisoned rows without changing the dispatch
/// surface or adding an endpoint.</summary>
public sealed class OutboxAdministrationTests
{
    [Fact]
    public async Task Redrive_resets_a_poisoned_row_and_discard_keeps_it_retired()
    {
        await using var host = await StartAsync();
        var administration = host.Services.GetRequiredService<IOutboxAdministration>();
        var poisoned = await InsertAsync(host, poisoned: true, type: "test.redrive", nextAttemptAt: host.Clock.UtcNow + TimeSpan.FromHours(6));

        var redriven = await administration.RedriveAsync([poisoned], CancellationToken.None);

        Assert.True(redriven.IsSuccess);
        Assert.Equal(OutboxAdministrationOutcome.Applied, Assert.Single(redriven.Value).Outcome);

        var redrivenRow = await administration.ListPoisonedAsync(10, CancellationToken.None);
        Assert.True(redrivenRow.IsSuccess);
        Assert.Empty(redrivenRow.Value);

        var rePoisoned = await InsertAsync(host, poisoned: true, type: "test.discard");
        var discarded = await administration.DiscardAsync([rePoisoned], "operator retired malformed payload", CancellationToken.None);

        Assert.True(discarded.IsSuccess);
        Assert.Equal(OutboxAdministrationOutcome.Applied, Assert.Single(discarded.Value).Outcome);
        Assert.Equal(
            OutboxAdministrationOutcome.NotPoisoned,
            Assert.Single((await administration.RedriveAsync([rePoisoned], CancellationToken.None)).Value).Outcome);
    }

    [Fact]
    public async Task Bulk_operations_are_scoped_to_type_and_listing_is_bounded_to_poisoned_rows()
    {
        await using var host = await StartAsync();
        var administration = host.Services.GetRequiredService<IOutboxAdministration>();
        var matching = await InsertAsync(host, poisoned: true, type: "test.matching");
        var other = await InsertAsync(host, poisoned: true, type: "test.other");
        await InsertAsync(host, poisoned: false, type: "test.matching");

        var redriven = await administration.RedriveByTypeAsync(new EventTypeName("test.matching"), CancellationToken.None);
        var listed = await administration.ListPoisonedAsync(1, CancellationToken.None);

        Assert.True(redriven.IsSuccess);
        Assert.Equal(1, redriven.Value);
        Assert.True(listed.IsSuccess);
        Assert.Equal(other, Assert.Single(listed.Value).Id);

        var discarded = await administration.DiscardByTypeAsync(new EventTypeName("test.other"), "retired", CancellationToken.None);
        Assert.True(discarded.IsSuccess);
        Assert.Equal(1, discarded.Value);
        Assert.Empty((await administration.ListPoisonedAsync(10, CancellationToken.None)).Value);
        Assert.NotEqual(matching, other);
    }

    [Fact]
    public async Task Per_id_redrive_reports_missing_and_non_poisoned_rows_without_failing_the_batch()
    {
        await using var host = await StartAsync();
        var administration = host.Services.GetRequiredService<IOutboxAdministration>();
        var store = host.Services.GetRequiredService<IOutboxStore>();
        var poisoned = new List<OutboxMessageId>();
        for (var index = 0; index < 39; index++)
        {
            poisoned.Add(await InsertAsync(host, poisoned: true, type: "test.batch"));
        }

        var missing = OutboxMessageId.Create(host.Clock.UtcNow + TimeSpan.FromMinutes(1));
        var batch = await administration.RedriveAsync([.. poisoned, missing], CancellationToken.None);

        Assert.True(batch.IsSuccess);
        Assert.Equal(39, batch.Value.Count(result => result.Outcome == OutboxAdministrationOutcome.Applied));
        Assert.Equal(OutboxAdministrationOutcome.NotFound, batch.Value.Single(result => result.Id == missing).Outcome);

        var pending = await InsertAsync(host, poisoned: false, type: "test.batch");
        var notPoisoned = await administration.RedriveAsync([pending], CancellationToken.None);
        Assert.Equal(OutboxAdministrationOutcome.NotPoisoned, Assert.Single(notPoisoned.Value).Outcome);

        var claimed = await store.ClaimNextAsync(new InstanceId("worker/test"), CancellationToken.None);
        Assert.True(claimed.IsSuccess);
        Assert.NotNull(claimed.Value);
        Assert.Contains(claimed.Value!.Id, poisoned);
    }

    [Fact]
    public async Task Type_operations_apply_to_500_poisoned_rows_without_touching_another_type()
    {
        await using var host = await StartAsync();
        var administration = host.Services.GetRequiredService<IOutboxAdministration>();
        for (var index = 0; index < 500; index++)
        {
            await InsertAsync(host, poisoned: true, type: "test.redrive-500");
            await InsertAsync(host, poisoned: true, type: "test.discard-500");
        }

        var redriven = await administration.RedriveByTypeAsync(new EventTypeName("test.redrive-500"), CancellationToken.None);
        var discarded = await administration.DiscardByTypeAsync(
            new EventTypeName("test.discard-500"), "operator retired batch", CancellationToken.None);

        Assert.Equal(500, redriven.Value);
        Assert.Equal(500, discarded.Value);
        Assert.Empty((await administration.ListPoisonedAsync(10, CancellationToken.None)).Value);
    }

    private static async Task<IPlatformTestHost> StartAsync()
    {
        var host = await PlatformTestHost.CreateBuilder()
            .WithRole(HostRole.Worker)
            .WithProvider(PersistenceProvider.Sqlite)
            .StartAsync(CancellationToken.None);
        Assert.True((await host.Services.GetRequiredService<IMigrationRunner>().ApplyAsync(CancellationToken.None)).IsSuccess);
        return host;
    }

    private static async Task<OutboxMessageId> InsertAsync(
        IPlatformTestHost host, bool poisoned, string type, DateTimeOffset? nextAttemptAt = null)
    {
        var id = OutboxMessageId.Create(host.Clock.UtcNow);
        var store = host.Services.GetRequiredService<IOutboxStore>();
        var inserted = await host.Services.GetRequiredService<IUnitOfWork>().ExecuteAsync(
            TransactionIntent.Write,
            token => store.InsertAsync(new OutboxMessage
            {
                Id = id,
                Sequence = 0,
                OccurredAt = host.Clock.UtcNow - TimeSpan.FromDays(3),
                Type = new EventTypeName(type),
                Payload = "{}",
                Tenant = TenantId.Implicit,
                TraceContext = new TraceContext("00-1111111111111111111111111111aaaa-2222222222222222-01", null),
                Correlation = new CorrelationId("3333333333333333333333333333bbbb"),
                Culture = CultureTag.Invariant,
                Attempts = poisoned ? 12 : 0,
                NextAttemptAt = nextAttemptAt,
                PoisonedAt = poisoned ? host.Clock.UtcNow : null,
                LastError = poisoned ? "original failure" : null,
            }, token),
            CancellationToken.None);
        Assert.True(inserted.IsSuccess);
        return id;
    }
}
