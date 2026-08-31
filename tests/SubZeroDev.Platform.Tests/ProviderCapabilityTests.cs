using System.Data.Common;
using Microsoft.Extensions.Logging.Abstractions;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Persistence;

namespace SubZeroDev.Platform.Tests;

/// <summary>Assertions over <see cref="IProviderCapability"/> alone — no connection, no schema.
/// These are the two correctness properties the design says nothing else would catch: the SQLite
/// instant and identifier encodings.</summary>
public sealed class ProviderCapabilityTests
{
    [Theory]
    [InlineData(PersistenceProvider.Sqlite)]
    [InlineData(PersistenceProvider.PostgreSql)]
    public void Identifiers_minted_at_distinct_instants_encode_in_mint_order(PersistenceProvider provider)
    {
        var capability = CreateCapability(provider);
        var clock = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        // Spans ten seconds, crossing the low-timestamp byte's wraparound repeatedly — the range
        // that makes the assertion meaningful; see the sibling test below for why a narrower one
        // would pass against a broken encoder too.
        var minted = new List<Guid>();
        for (var i = 0; i < 100; i++)
        {
            minted.Add(Guid.CreateVersion7(clock));
            clock = clock.AddMilliseconds(100);
        }

        var encoded = minted.Select(capability.EncodeIdentifier).ToList();
        var sortedByBlob = encoded.OrderBy(bytes => bytes, ByteArrayComparer.Instance).ToList();

        Assert.Equal(encoded, sortedByBlob);

        foreach (var (id, blob) in minted.Zip(encoded))
        {
            Assert.True(capability.TryDecodeIdentifier(blob, out var decoded));
            Assert.Equal(id, decoded);
        }
    }

    [Fact]
    public void The_mint_order_assertion_goes_red_against_the_platforms_native_byte_order()
    {
        // What EncodeIdentifier exists to prevent: Guid.ToByteArray() stores Data1/2/3
        // little-endian, which scrambles a version-7 UUID's time-ordered prefix.
        // A 100 ms spread never crosses the 16-bit low-timestamp byte's wraparound, so the
        // platform's little-endian layout happens to still sort right — the false negative the
        // assertion table warns about. A 100 ms step over 100 ids spans ten seconds, comfortably
        // crossing it.
        var clock = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var minted = new List<Guid>();
        for (var i = 0; i < 100; i++)
        {
            minted.Add(Guid.CreateVersion7(clock));
            clock = clock.AddMilliseconds(100);
        }

        var brokenlyEncoded = minted.Select(id => id.ToByteArray()).ToList();
        var sortedByBlob = brokenlyEncoded.OrderBy(bytes => bytes, ByteArrayComparer.Instance).ToList();

        Assert.NotEqual(brokenlyEncoded, sortedByBlob);
    }

    [Theory]
    [InlineData(PersistenceProvider.Sqlite)]
    [InlineData(PersistenceProvider.PostgreSql)]
    public void Instants_compare_correctly_across_a_sub_second_boundary(PersistenceProvider provider)
    {
        var capability = CreateCapability(provider);

        var earlier = new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero).AddTicks(1_000_000); // .1000000Z
        var later = new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero).AddTicks(1_500_000); // .1500000Z
        var comparand = new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero).AddTicks(1_200_000); // .1200000Z

        var formattedEarlier = capability.FormatInstant(earlier);
        var formattedLater = capability.FormatInstant(later);
        var formattedComparand = capability.FormatInstant(comparand);

        Assert.True(string.CompareOrdinal(formattedEarlier, formattedComparand) < 0);
        Assert.True(string.CompareOrdinal(formattedComparand, formattedLater) < 0);

        Assert.True(capability.TryParseInstant(formattedEarlier, out var roundTripped));
        Assert.Equal(earlier, roundTripped);
    }

    [Fact]
    public void The_comparison_assertion_goes_red_against_a_trimming_formatter()
    {
        // What the fixed-width, seven-digit rule exists to prevent: a formatter that trims
        // trailing zeros makes ".1Z" sort after ".12Z" — later in the string, earlier in time.
        const string trimmedEarlier = "2026-08-03T12:00:00.1Z";
        const string trimmedComparand = "2026-08-03T12:00:00.12Z";

        Assert.True(string.CompareOrdinal(trimmedEarlier, trimmedComparand) > 0);
    }

    [Theory]
    [InlineData(PersistenceProvider.Sqlite)]
    [InlineData(PersistenceProvider.PostgreSql)]
    public void Migration_history_table_names_are_safe_regardless_of_module_name(PersistenceProvider provider)
    {
        var capability = CreateCapability(provider);

        // ModuleName only requires non-empty and trims — it does not forbid spaces, punctuation, or
        // anything else a developer types — and the result is interpolated unquoted into DDL.
        var table = capability.MigrationHistoryTable(new ModuleName("Orders; DROP TABLE t--"));

        Assert.Matches("^[a-z0-9_]+$", table);
        Assert.StartsWith("platform_migrations_", table, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PersistenceProvider.Sqlite)]
    [InlineData(PersistenceProvider.PostgreSql)]
    public void Module_names_differing_only_in_unsafe_characters_still_collide_predictably(PersistenceProvider provider)
    {
        var capability = CreateCapability(provider);

        var withSpace = capability.MigrationHistoryTable(new ModuleName("Order Processing"));
        var withHyphen = capability.MigrationHistoryTable(new ModuleName("Order-Processing"));

        // Both collapse every non-alphanumeric run to one underscore, so these collide — which is
        // exactly what the migration runner's own collision guard exists to catch before anything
        // is applied, rather than corrupting two modules' histories silently.
        Assert.Equal(withSpace, withHyphen);
    }

    [Fact]
    public async Task Unit_of_work_accepts_a_transaction_implemented_only_through_the_public_contract()
    {
        var capability = new PublicTransactionCapability(new SqliteProviderCapability(
            new PersistenceOptions
            {
                Provider = PersistenceProvider.Sqlite,
                ConnectionString = "Data Source=:memory:;Pooling=False",
            }));
        var auditDispatcher = new AuditSinkDispatcher(
            new AuditSinkRegistry(), new AuditSinkHealthState(), NullLogger<AuditSinkDispatcher>.Instance);
        var unitOfWork = new UnitOfWork(
            capability, new AmbientTransactionState(), new FakeOutboxStore(), auditDispatcher);

        var result = await unitOfWork.ExecuteAsync(
            TransactionIntent.Write,
            _ => Task.CompletedTask,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
    }

    private static IProviderCapability CreateCapability(PersistenceProvider provider) => provider switch
    {
        PersistenceProvider.Sqlite => new SqliteProviderCapability(
            new PersistenceOptions { Provider = provider, ConnectionString = "Data Source=:memory:" }),
        PersistenceProvider.PostgreSql => new PostgreSqlProviderCapability(
            new PersistenceOptions { Provider = provider, ConnectionString = "Host=localhost;Database=unused" }),
        _ => throw new NotSupportedException($"No capability for '{provider}'."),
    };

    /// <summary>Delegates every provider operation, but deliberately replaces the concrete
    /// transaction with one implemented only through the public seam.</summary>
    private sealed class PublicTransactionCapability(IProviderCapability inner) : IProviderCapability
    {
        public PersistenceProvider Provider => inner.Provider;

        public string FormatInstant(DateTimeOffset instant) => inner.FormatInstant(instant);

        public bool TryParseInstant(string stored, out DateTimeOffset instant) =>
            inner.TryParseInstant(stored, out instant);

        public byte[] EncodeIdentifier(Guid value) => inner.EncodeIdentifier(value);

        public bool TryDecodeIdentifier(ReadOnlySpan<byte> encoded, out Guid value) =>
            inner.TryDecodeIdentifier(encoded, out value);

        public string MigrationHistoryTable(ModuleName module) => inner.MigrationHistoryTable(module);

        public async Task<Result<IAmbientTransaction, TransactionError>> BeginAsync(
            TransactionIntent intent,
            CancellationToken cancellationToken)
        {
            var opened = await inner.BeginAsync(intent, cancellationToken);
            return opened.IsSuccess
                ? Result<IAmbientTransaction, TransactionError>.Success(new PublicAmbientTransaction(
                    opened.Value.Intent,
                    opened.Value.Connection,
                    opened.Value.Transaction))
                : Result<IAmbientTransaction, TransactionError>.Failure(opened.Error);
        }

        public TransactionError Classify(Exception exception) => inner.Classify(exception);

        public Task<Result<OutboxMessageId?, TransactionError>> StampClaimAsync(
            InstanceId holder,
            DateTimeOffset now,
            TimeSpan claimWindow,
            CancellationToken cancellationToken) =>
            inner.StampClaimAsync(holder, now, claimWindow, cancellationToken);

        public Task<Result<IMigrationLock, MigrationError>> AcquireMigrationLockAsync(
            CancellationToken cancellationToken) => inner.AcquireMigrationLockAsync(cancellationToken);

        public Task<Result<ConfigurationError>> AssertStartupPreconditionsAsync(
            CancellationToken cancellationToken) => inner.AssertStartupPreconditionsAsync(cancellationToken);

        public Task<Result<int, TransactionError>> DeleteBoundedAsync(
            PruneTarget target,
            DateTimeOffset olderThan,
            int batchSize,
            CancellationToken cancellationToken) =>
            inner.DeleteBoundedAsync(target, olderThan, batchSize, cancellationToken);
    }

    private sealed record PublicAmbientTransaction(
        TransactionIntent Intent,
        DbConnection Connection,
        DbTransaction Transaction) : IAmbientTransaction;
}
