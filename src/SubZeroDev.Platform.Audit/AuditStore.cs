using System.Data.Common;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Persistence;

namespace SubZeroDev.Platform.Audit;

/// <summary>Reads and writes the one <c>audit_event</c> table exactly as <c>design/20-contract.md</c>,
/// Persisted schemas §1 names its columns — the actor stored as two columns, the primary key the
/// event id with no tenant prefix (S13.5), and no unique constraint spanning hosts (S13.7). Every
/// member is handed the connection and transaction to use rather than reading
/// <see cref="IAmbientTransactionAccessor"/> itself, because <see cref="DurableAuditSink"/> writes
/// both inside the ambient transaction, when one is open, and against a transaction of its own, when
/// none is (*Public surface* §6: a denial or a read dispatches in its own transaction).</summary>
internal sealed class AuditStore(IProviderCapability capability)
{
    internal async Task InsertAsync(
        DbConnection connection, DbTransaction transaction, AuditEvent auditEvent, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO audit_event
                (event_id, occurred_at, actor_issuer, actor_subject, actor_kind, tenant, action,
                 resource_type, resource_id, outcome, correlation, class)
            VALUES
                (@eventId, @occurredAt, @actorIssuer, @actorSubject, @actorKind, @tenant, @action,
                 @resourceType, @resourceId, @outcome, @correlation, @class);
            """;

        AddParameter(command, "@eventId", capability.EncodeIdentifier(auditEvent.Id.Value));
        AddParameter(command, "@occurredAt", capability.FormatInstant(auditEvent.OccurredAt));
        AddParameter(command, "@actorIssuer", auditEvent.Actor.Issuer);
        AddParameter(command, "@actorSubject", auditEvent.Actor.Subject);
        AddParameter(command, "@actorKind", auditEvent.ActorKind.ToString());
        AddParameter(command, "@tenant", auditEvent.Tenant.ToString());
        AddParameter(command, "@action", auditEvent.Action.Value);
        AddParameter(command, "@resourceType", auditEvent.Resource?.Type);
        AddParameter(command, "@resourceId", auditEvent.Resource?.Id);
        AddParameter(command, "@outcome", auditEvent.Outcome.ToString());
        AddParameter(command, "@correlation", auditEvent.Correlation.TraceId);
        AddParameter(command, "@class", auditEvent.Class.ToString());

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task<IReadOnlyList<AuditEvent>> QueryAsync(
        DbConnection connection,
        DbTransaction transaction,
        string whereClause,
        Action<DbCommand> bind,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT event_id, occurred_at, actor_issuer, actor_subject, actor_kind, tenant, action,
                   resource_type, resource_id, outcome, correlation, class
            FROM audit_event
            WHERE {whereClause}
            ORDER BY occurred_at;
            """;
        bind(command);

        var events = new List<AuditEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            events.Add(ReadRow(reader));
        }

        return events;
    }

    private AuditEvent ReadRow(DbDataReader reader)
    {
        var idBytes = (byte[])reader[0];
        capability.TryDecodeIdentifier(idBytes, out var idValue);
        capability.TryParseInstant(reader.GetString(1), out var occurredAt);

        var resourceType = reader.IsDBNull(7) ? null : reader.GetString(7);
        var resourceId = reader.IsDBNull(8) ? null : reader.GetString(8);

        return new AuditEvent(
            new AuditEventId(idValue),
            occurredAt,
            new PrincipalId(reader.GetString(2), reader.GetString(3)),
            Enum.Parse<PrincipalKind>(reader.GetString(4)),
            new TenantId(Guid.Parse(reader.GetString(5))),
            new AuditAction(reader.GetString(6)),
            resourceType is null ? null : new ResourceRef(resourceType, resourceId!),
            Enum.Parse<AuditOutcome>(reader.GetString(9)),
            new CorrelationId(reader.GetString(10)),
            Enum.Parse<AuditClass>(reader.GetString(11)));
    }

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}

/// <summary>Creates <c>audit_event</c> — the one table <c>design/20-contract.md</c>'s Persisted
/// schemas §1 names. <b>Migration story: none</b> beyond this: no retention migration is ever
/// registered, and the table grows without bound deliberately
/// (<c>design/90-decisions.md</c> § Open).</summary>
internal sealed class AuditMigrationSource : IModuleMigrationSource
{
    public ModuleName Module { get; } = new("Audit");

    public IReadOnlyList<IModuleMigration> Migrations { get; } = [new CreateAuditTableMigration()];
}

internal sealed class CreateAuditTableMigration : IModuleMigration
{
    public string Name => "0001_create_audit_event";

    public async Task ApplyAsync(
        DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken)
    {
        var isSqlite = connection is Microsoft.Data.Sqlite.SqliteConnection;
        var blobType = isSqlite ? "BLOB" : "BYTEA";

        // The primary key is the event id, with no tenant prefix (S13.5): an audit row is written
        // about a tenant, not owned by one, and a tenant-keyed key would turn an operator's
        // cross-tenant query into a cross-partition scan. Exactly the three indexes the brief's
        // Audit criterion and the shell's audit view read — nothing else, because every index is a
        // write cost on the path every security-sensitive action takes (S13.3). No unique constraint
        // spans hosts, so concurrent appends from two hosts never contend (S13.7). The actor is two
        // columns — issuer and subject — because PrincipalId.ToString() is not injective and must
        // never be split to recover the pair (I-I3, S13.2). The two resource columns are null
        // together or present together (design/20-contract.md, Persisted schemas §1).
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"""
                CREATE TABLE audit_event (
                    event_id {blobType} NOT NULL,
                    occurred_at TEXT NOT NULL,
                    actor_issuer TEXT NOT NULL,
                    actor_subject TEXT NOT NULL,
                    actor_kind TEXT NOT NULL,
                    tenant TEXT NOT NULL,
                    action TEXT NOT NULL,
                    resource_type TEXT NULL,
                    resource_id TEXT NULL,
                    outcome TEXT NOT NULL,
                    correlation TEXT NOT NULL,
                    class TEXT NOT NULL,
                    PRIMARY KEY (event_id),
                    CHECK ((resource_type IS NULL) = (resource_id IS NULL))
                );
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var tenantIndex = connection.CreateCommand())
        {
            tenantIndex.Transaction = transaction;
            tenantIndex.CommandText =
                "CREATE INDEX ix_audit_event_tenant_occurred_at ON audit_event (tenant, occurred_at);";
            await tenantIndex.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var correlationIndex = connection.CreateCommand())
        {
            correlationIndex.Transaction = transaction;
            correlationIndex.CommandText = "CREATE INDEX ix_audit_event_correlation ON audit_event (correlation);";
            await correlationIndex.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var actorIndex = connection.CreateCommand())
        {
            actorIndex.Transaction = transaction;
            actorIndex.CommandText =
                "CREATE INDEX ix_audit_event_actor_subject_occurred_at ON audit_event (actor_subject, occurred_at);";
            await actorIndex.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
