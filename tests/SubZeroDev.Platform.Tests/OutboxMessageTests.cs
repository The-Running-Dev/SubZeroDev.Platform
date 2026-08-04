using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Persistence;

namespace SubZeroDev.Platform.Tests;

/// <summary>Assertions over <see cref="OutboxMessage"/> alone — no connection, no schema. The state
/// and due-at derivations are the two members this slice's Persistence surface adds beyond the raw
/// columns, and both are pure functions of the record.</summary>
public sealed class OutboxMessageTests
{
    [Fact]
    public void A_freshly_built_message_is_pending_and_due_at_occurred_at()
    {
        var occurredAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var message = Build(occurredAt);

        Assert.Equal(OutboxMessageState.Pending, message.State);
        Assert.Equal(occurredAt, message.DueAt);
    }

    [Fact]
    public void Due_at_prefers_next_attempt_at_when_set()
    {
        var occurredAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var nextAttempt = occurredAt.AddMinutes(5);
        var message = Build(occurredAt) with { NextAttemptAt = nextAttempt };

        Assert.Equal(nextAttempt, message.DueAt);
    }

    [Theory]
    [InlineData(false, false, OutboxMessageState.Pending)]
    [InlineData(true, false, OutboxMessageState.Processed)]
    [InlineData(false, true, OutboxMessageState.Poisoned)]
    [InlineData(true, true, OutboxMessageState.Discarded)]
    public void State_is_derived_from_the_two_mark_columns_and_never_stored(bool processed, bool poisoned, OutboxMessageState expected)
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var message = Build(now) with
        {
            ProcessedAt = processed ? now : null,
            PoisonedAt = poisoned ? now : null,
            LastError = poisoned ? "boom" : null,
        };

        Assert.Equal(expected, message.State);
    }

    [Fact]
    public void Ids_minted_from_the_same_instant_are_version_seven_uuids()
    {
        var at = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var id = OutboxMessageId.Create(at);

        Assert.Equal(7, id.Value.Version);
    }

    private static OutboxMessage Build(DateTimeOffset occurredAt) => new()
    {
        Id = OutboxMessageId.Create(occurredAt),
        Sequence = 1,
        OccurredAt = occurredAt,
        Type = new EventTypeName("test.event"),
        Payload = "{}",
        Tenant = TenantId.Implicit,
        TraceContext = new TraceContext("00-1111111111111111111111111111aaaa-2222222222222222-01", null),
        Correlation = new CorrelationId("3333333333333333333333333333bbbb"),
        Culture = CultureTag.Invariant,
        Attempts = 0,
    };
}
