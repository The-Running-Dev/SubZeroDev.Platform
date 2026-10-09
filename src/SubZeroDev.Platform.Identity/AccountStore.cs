using System.Data.Common;
using Microsoft.Data.Sqlite;
using Npgsql;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Persistence;

namespace SubZeroDev.Platform.Identity;

/// <summary>Reads and writes <c>identity_account</c> and <c>identity_account_link</c> inside the
/// ambient transaction. Every method requires one.</summary>
internal sealed class AccountStore(IAmbientTransactionAccessor ambient, IProviderCapability capability)
{
    /// <summary>Inserts one account row.</summary>
    internal async Task InsertAccountAsync(
        AccountId account, DateTimeOffset createdAt, CancellationToken cancellationToken)
    {
        var current = Current();
        await using var command = current.Connection.CreateCommand();
        command.Transaction = current.Transaction;
        command.CommandText = """
            INSERT INTO identity_account (id, created_at)
            VALUES (@id, @createdAt);
            """;
        AddParameter(command, "@id", account.Value);
        AddParameter(command, "@createdAt", capability.FormatInstant(createdAt));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Inserts one link row. A pair already linked, to any account, violates the link's
    /// primary key (I-I16) and throws the raw provider exception (<see cref="IsUniqueViolation"/>):
    /// the caller lets it unwind through <see cref="IUnitOfWork.ExecuteAsync{T}"/>, which rolls the
    /// transaction back, and recovers the cause from a flag set in its <c>catch when</c>.</summary>
    internal async Task InsertLinkAsync(
        PrincipalId identity, AccountId account, DateTimeOffset linkedAt, CancellationToken cancellationToken)
    {
        var current = Current();
        await using var command = current.Connection.CreateCommand();
        command.Transaction = current.Transaction;
        command.CommandText = """
            INSERT INTO identity_account_link (issuer, subject, account, linked_at)
            VALUES (@issuer, @subject, @account, @linkedAt);
            """;
        AddParameter(command, "@issuer", identity.Issuer);
        AddParameter(command, "@subject", identity.Subject);
        AddParameter(command, "@account", account.Value);
        AddParameter(command, "@linkedAt", capability.FormatInstant(linkedAt));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Whether an exception raised while writing is a unique-constraint violation. The
    /// account's own key is 128 random bits, so on these inserts it is the link's primary key.</summary>
    internal static bool IsUniqueViolation(Exception exception) => exception switch
    {
        SqliteException sqlite => sqlite.SqliteErrorCode == 19, // SQLITE_CONSTRAINT
        PostgresException postgres => postgres.SqlState == "23505", // unique_violation
        _ => false,
    };

    /// <summary>The account an (issuer, subject) pair is linked to, matched by ordinal equality on
    /// both halves (I-I2), or <see langword="null"/> when the pair is unlinked.</summary>
    internal async Task<AccountId?> FindLinkedAccountAsync(
        PrincipalId identity, CancellationToken cancellationToken)
    {
        var current = Current();
        await using var command = current.Connection.CreateCommand();
        command.Transaction = current.Transaction;
        command.CommandText = """
            SELECT account
            FROM identity_account_link
            WHERE issuer = @issuer AND subject = @subject;
            """;
        AddParameter(command, "@issuer", identity.Issuer);
        AddParameter(command, "@subject", identity.Subject);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new AccountId(reader.GetString(0))
            : null;
    }

    /// <summary>Reads one account and its linked identities, or <see langword="null"/> when no such
    /// account exists.</summary>
    internal async Task<Account?> FindAccountAsync(AccountId account, CancellationToken cancellationToken)
    {
        var current = Current();
        DateTimeOffset createdAt;

        await using (var command = current.Connection.CreateCommand())
        {
            command.Transaction = current.Transaction;
            command.CommandText = """
                SELECT created_at
                FROM identity_account
                WHERE id = @id;
                """;
            AddParameter(command, "@id", account.Value);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            capability.TryParseInstant(reader.GetString(0), out createdAt);
        }

        List<LinkedIdentity> identities = [];
        await using (var command = current.Connection.CreateCommand())
        {
            command.Transaction = current.Transaction;
            command.CommandText = """
                SELECT issuer, subject, linked_at
                FROM identity_account_link
                WHERE account = @account;
                """;
            AddParameter(command, "@account", account.Value);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                capability.TryParseInstant(reader.GetString(2), out var linkedAt);
                identities.Add(new LinkedIdentity(new PrincipalId(reader.GetString(0), reader.GetString(1)), linkedAt));
            }
        }

        // Ordered here rather than in SQL: the order is ordinal on issuer and subject (I-I19), which
        // a database collation does not promise.
        identities.Sort(static (left, right) =>
        {
            var byTime = left.LinkedAt.CompareTo(right.LinkedAt);
            if (byTime != 0)
            {
                return byTime;
            }

            var byIssuer = string.CompareOrdinal(left.Identity.Issuer, right.Identity.Issuer);
            return byIssuer != 0 ? byIssuer : string.CompareOrdinal(left.Identity.Subject, right.Identity.Subject);
        });

        return new Account(account, createdAt, identities);
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

/// <summary>Creates <c>identity_account</c> and <c>identity_account_link</c>. In the
/// <c>IdentityAccounts</c> module's own sequence, so only a host that registers
/// <see cref="IdentityAccountsModule"/> runs it (I-I21).</summary>
internal sealed class IdentityAccountsMigrationSource : IModuleMigrationSource
{
    public ModuleName Module { get; } = new("IdentityAccounts");

    public IReadOnlyList<IModuleMigration> Migrations { get; } = [new CreateAccountTablesMigration()];
}

internal sealed class CreateAccountTablesMigration : IModuleMigration
{
    public string Name => "0001_create_accounts";

    public async Task ApplyAsync(DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken)
    {
        // Exactly these columns, and none may be added: no email, profile, credential, token or claim
        // (I-I4, I-I17). No tenant column — an account is per host.
        await ExecuteAsync(connection, transaction, """
            CREATE TABLE identity_account (
                id TEXT NOT NULL,
                created_at TEXT NOT NULL,
                PRIMARY KEY (id)
            );
            """, cancellationToken).ConfigureAwait(false);

        // Keyed by the pair as the token carried it, two columns (I-I3). The primary key is what makes
        // one identity belong to at most one account (I-I16).
        await ExecuteAsync(connection, transaction, """
            CREATE TABLE identity_account_link (
                issuer TEXT NOT NULL,
                subject TEXT NOT NULL,
                account TEXT NOT NULL REFERENCES identity_account (id),
                linked_at TEXT NOT NULL,
                PRIMARY KEY (issuer, subject)
            );
            """, cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(connection, transaction, """
            CREATE INDEX identity_account_link_account ON identity_account_link (account);
            """, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExecuteAsync(
        DbConnection connection, DbTransaction transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
