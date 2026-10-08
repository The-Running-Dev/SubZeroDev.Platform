using System.Data.Common;
using SubZeroDev.Platform.Abstractions;

namespace SubZeroDev.Platform.Persistence;

/// <summary>Records that a handler's transaction committed for one (message, consumer), so a
/// redelivered message is skipped rather than applied twice (I-P4). Not a callable seam: the outbox
/// dispatcher is its only caller, and it writes on the dispatcher's own unit of work so the record
/// commits — or rolls back — with the handler's effects (I-P5).</summary>
internal sealed class InboxStore(IAmbientTransactionAccessor ambient, IProviderCapability capability)
{
    /// <summary>INSERT … ON CONFLICT (message_id, consumer) DO NOTHING on the ambient transaction.
    /// True when the record was inserted; false when one already existed. On PostgreSQL a second
    /// insert of an uncommitted key waits for the first transaction, then inserts nothing if it
    /// committed; on SQLite the second write transaction cannot begin until the first ends.</summary>
    internal async Task<bool> TryRecordAsync(
        OutboxMessageId message, EventTypeName consumer, TenantId tenant, DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        var current = ambient.Current
            ?? throw new InvalidOperationException("The inbox records only inside a unit of work.");

        await using var command = current.Connection.CreateCommand();
        command.Transaction = current.Transaction;
        command.CommandText = """
            INSERT INTO platform_inbox (message_id, consumer, tenant, processed_at)
            VALUES (@messageId, @consumer, @tenant, @processedAt)
            ON CONFLICT (message_id, consumer) DO NOTHING;
            """;
        AddParameter(command, "@messageId", capability.EncodeIdentifier(message.Value));
        AddParameter(command, "@consumer", consumer.Value);
        AddParameter(command, "@tenant", tenant.ToString());
        AddParameter(command, "@processedAt", capability.FormatInstant(at));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}

/// <summary>Creates <c>platform_inbox</c>, appended to <see cref="PlatformMigrationSource"/> for the
/// same reason <see cref="CreateBackgroundWorkLeaseTable"/> is. No foreign key to
/// <c>platform_outbox</c>: an orphaned record is pruned instead.</summary>
internal sealed class CreateInboxTable : IModuleMigration
{
    public string Name => "0004_create_inbox";

    public async Task ApplyAsync(DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken)
    {
        // The message id uses the outbox's identifier encoding, so only its declared type branches.
        var blobType = connection is Microsoft.Data.Sqlite.SqliteConnection ? "BLOB" : "BYTEA";

        await using var createTable = connection.CreateCommand();
        createTable.Transaction = transaction;
        createTable.CommandText = $"""
            CREATE TABLE platform_inbox (
                message_id {blobType} NOT NULL,
                consumer TEXT NOT NULL,
                tenant TEXT NOT NULL,
                processed_at TEXT NOT NULL,
                PRIMARY KEY (message_id, consumer)
            );
            """;
        await createTable.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
