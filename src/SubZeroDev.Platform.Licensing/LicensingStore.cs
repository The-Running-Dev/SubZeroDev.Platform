using System.Data.Common;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Persistence;

namespace SubZeroDev.Platform.Licensing;

/// <summary>One stored verified-licence row, exactly as <c>design/20-contract.md</c>, Persisted
/// schemas §4 names its columns.</summary>
/// <param name="Claims">The claims the row carries.</param>
/// <param name="DocumentFingerprint">A fingerprint of the document verified.</param>
/// <param name="KeyId">The id of the key that verified it.</param>
internal sealed record VerifiedLicenceRecord(
    LicenceClaims Claims,
    string DocumentFingerprint,
    string KeyId);

/// <summary>Reads and writes the one verified-licence row. Every member enlists against the ambient
/// transaction the caller opened via <see cref="IUnitOfWork"/> — never a connection of its own — the
/// same discipline <c>BillingStore</c> follows.</summary>
/// <remarks><b>One row for the installation</b>, keyed by <see cref="SingleRowKey"/>: not one per
/// host, not one per tenant (I-L1). The installation is the database, not the machine, so several
/// hosts sharing a store share the row and converge on the newest verification.</remarks>
internal sealed class LicensingStore(IAmbientTransactionAccessor ambient, IProviderCapability capability)
{
    /// <summary>The fixed single-row key. A literal rather than a mint, because there is exactly one
    /// row and a generated key would permit a second.</summary>
    internal const string SingleRowKey = "installation";

    /// <summary>The feature-list separator. A newline cannot occur in a
    /// <see cref="FeatureName"/> that any caller would write, and the whole installation is one row,
    /// so the granted features are one column rather than a fan-out table.</summary>
    private const char FeatureSeparator = '\n';

    /// <summary>Reads the stored row, or null when the installation has none. <b>No row means
    /// <see cref="LicenceTier.Community"/></b> — that is the fallback rule and the fresh-installation
    /// rule at once, not two rules (S12.5).</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The row, or null.</returns>
    internal async Task<VerifiedLicenceRecord?> FindAsync(CancellationToken cancellationToken)
    {
        var current = Current();
        await using var command = current.Connection.CreateCommand();
        command.Transaction = current.Transaction;
        command.CommandText = """
            SELECT tier, features, issued_at, expires_at, grace_ends_at, verified_at,
                   document_fingerprint, key_id
            FROM verified_licence
            WHERE installation_key = @key;
            """;
        AddParameter(command, "@key", SingleRowKey);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var tier = new LicenceTier(reader.GetString(0));
        var features = reader.GetString(1)
            .Split(FeatureSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(name => new FeatureName(name))
            .ToHashSet();

        capability.TryParseInstant(reader.GetString(2), out var issuedAt);
        DateTimeOffset? expiresAt = reader.IsDBNull(3)
            ? null
            : capability.TryParseInstant(reader.GetString(3), out var parsedExpiry) ? parsedExpiry : null;
        DateTimeOffset? graceEndsAt = reader.IsDBNull(4)
            ? null
            : capability.TryParseInstant(reader.GetString(4), out var parsedGrace) ? parsedGrace : null;
        capability.TryParseInstant(reader.GetString(5), out var verifiedAt);

        return new VerifiedLicenceRecord(
            new LicenceClaims(tier, features, issuedAt, expiresAt, graceEndsAt, verifiedAt),
            reader.GetString(6),
            reader.GetString(7));
    }

    /// <summary>Writes the row, replacing whatever stood before. Called <b>only</b> after the caller
    /// has compared verification instants inside the same transaction (I-L3) and only on the
    /// <see cref="LicenceVerificationOutcome.Verified"/> path — <b>no error path writes any
    /// column</b> (I-L2, S12.3), which is what makes stored grace unextendable.</summary>
    /// <param name="record">The row to write.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the write does.</returns>
    internal async Task WriteAsync(VerifiedLicenceRecord record, CancellationToken cancellationToken)
    {
        var current = Current();

        await using (var delete = current.Connection.CreateCommand())
        {
            delete.Transaction = current.Transaction;
            delete.CommandText = "DELETE FROM verified_licence WHERE installation_key = @key;";
            AddParameter(delete, "@key", SingleRowKey);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var insert = current.Connection.CreateCommand();
        insert.Transaction = current.Transaction;
        insert.CommandText = """
            INSERT INTO verified_licence
                (installation_key, tier, features, issued_at, expires_at, grace_ends_at, verified_at,
                 document_fingerprint, key_id)
            VALUES
                (@key, @tier, @features, @issuedAt, @expiresAt, @graceEndsAt, @verifiedAt,
                 @fingerprint, @keyId);
            """;

        var claims = record.Claims;
        AddParameter(insert, "@key", SingleRowKey);
        AddParameter(insert, "@tier", claims.Tier.Value);
        AddParameter(
            insert,
            "@features",
            string.Join(FeatureSeparator, claims.Features.Select(feature => feature.Value).Order(StringComparer.Ordinal)));
        AddParameter(insert, "@issuedAt", capability.FormatInstant(claims.IssuedAt));
        AddParameter(
            insert, "@expiresAt", claims.ExpiresAt is { } expiry ? capability.FormatInstant(expiry) : null);
        AddParameter(
            insert, "@graceEndsAt", claims.GraceEndsAt is { } grace ? capability.FormatInstant(grace) : null);
        AddParameter(insert, "@verifiedAt", capability.FormatInstant(claims.VerifiedAt));
        AddParameter(insert, "@fingerprint", record.DocumentFingerprint);
        AddParameter(insert, "@keyId", record.KeyId);

        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private IAmbientTransaction Current() =>
        ambient.Current ?? throw new PlatformContractViolationException(ContractViolation.NoAmbientTransaction());

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}

/// <summary>Creates <c>verified_licence</c> — the one table <c>design/20-contract.md</c>'s Persisted
/// schemas §4 names. <b>Migration story: none</b> beyond this: a fresh installation has no row, and
/// no row means <see cref="LicenceTier.Community"/>.</summary>
internal sealed class LicensingMigrationSource : IModuleMigrationSource
{
    public ModuleName Module { get; } = new("Licensing");

    public IReadOnlyList<IModuleMigration> Migrations { get; } = [new CreateLicensingTablesMigration()];
}

internal sealed class CreateLicensingTablesMigration : IModuleMigration
{
    public string Name => "0001_create_licensing";

    public async Task ApplyAsync(
        DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken)
    {
        // One row for the installation, keyed by a fixed single-row key (I-L1). The primary key is
        // what makes "one row" structural rather than a convention the next writer can break: the
        // installation is the database, so several hosts contend on this one row and the monotonic
        // guard decides which write stands.
        //
        // expires_at and grace_ends_at hold the instants computed AT VERIFICATION, never derived from
        // when an error happened — a deployment that errors on every verification cannot renew its
        // own grace, because nothing on any error path writes these columns (I-L2).
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE verified_licence (
                installation_key TEXT NOT NULL,
                tier TEXT NOT NULL,
                features TEXT NOT NULL,
                issued_at TEXT NOT NULL,
                expires_at TEXT NULL,
                grace_ends_at TEXT NULL,
                verified_at TEXT NOT NULL,
                document_fingerprint TEXT NOT NULL,
                key_id TEXT NOT NULL,
                PRIMARY KEY (installation_key)
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
