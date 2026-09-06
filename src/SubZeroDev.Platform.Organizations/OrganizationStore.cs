using System.Data.Common;
using Microsoft.Data.Sqlite;
using Npgsql;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Persistence;

namespace SubZeroDev.Platform.Organizations;

/// <summary>Mints the tenant a new organization will hold. A seam, not inlined
/// <c>Guid.NewGuid()</c> calls, so a test can force two creates to race for the identical tenant and
/// observe <see cref="OrganizationError.TenantAlreadyAssigned"/> (S10.2) without depending on a real
/// GUID collision.</summary>
public interface IOrganizationTenantMinter
{
    /// <summary>Mints a fresh tenant identifier.</summary>
    /// <returns>The tenant.</returns>
    TenantId Mint();
}

/// <inheritdoc cref="IOrganizationTenantMinter"/>
internal sealed class GuidOrganizationTenantMinter : IOrganizationTenantMinter
{
    public TenantId Mint() => new(Guid.NewGuid());
}

/// <summary>Stores organization, membership and invitation rows. Every member enlists against the
/// ambient transaction the caller (<see cref="OrganizationApi"/>) already opened via
/// <see cref="IUnitOfWork"/> — never a connection of its own — so a create's tenant, organization,
/// owner membership and audit row (Persistence's own enlistment) commit or roll back as one (S10.1,
/// I-O1).</summary>
internal sealed class OrganizationStore(IAmbientTransactionAccessor ambient, IProviderCapability capability)
{
    /// <summary>Inserts one organization row. Throws the raw provider exception on a unique-tenant
    /// violation (<see cref="IsUniqueViolation"/>) rather than swallowing it — swallowing it here
    /// would leave a PostgreSQL transaction aborted for every statement that followed; the caller
    /// lets the exception unwind through <see cref="IUnitOfWork.ExecuteAsync{T}"/>, which rolls the
    /// whole transaction back the normal way, and inspects a flag it set from the matching
    /// <c>catch when</c> to recover which cause applied (S10.2).</summary>
    internal async Task InsertOrganizationAsync(Organization organization, CancellationToken cancellationToken)
    {
        var current = Current();
        await using var command = current.Connection.CreateCommand();
        command.Transaction = current.Transaction;
        command.CommandText = """
            INSERT INTO organization (id, tenant, name, owner_issuer, owner_subject, created_at)
            VALUES (@id, @tenant, @name, @ownerIssuer, @ownerSubject, @createdAt);
            """;
        AddParameter(command, "@id", capability.EncodeIdentifier(organization.Id.Value));
        AddParameter(command, "@tenant", organization.Tenant.ToString());
        AddParameter(command, "@name", organization.Name);
        AddParameter(command, "@ownerIssuer", organization.Owner.Issuer);
        AddParameter(command, "@ownerSubject", organization.Owner.Subject);
        AddParameter(command, "@createdAt", capability.FormatInstant(organization.CreatedAt));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Whether an exception raised while writing is the unique-tenant-constraint violation
    /// I-O2 relies on. The organization table's only other constraint is its primary key on a freshly
    /// minted GUID, which does not collide in practice, so any unique-constraint violation on this
    /// insert is the tenant race.</summary>
    internal static bool IsUniqueViolation(Exception exception) => exception switch
    {
        SqliteException sqlite => sqlite.SqliteErrorCode == 19, // SQLITE_CONSTRAINT
        PostgresException postgres => postgres.SqlState == "23505", // unique_violation
        _ => false,
    };

    /// <summary>Inserts or re-activates a membership row. Upserts on the (organization, issuer,
    /// subject) key rather than only inserting, so redeeming a fresh invitation after a prior
    /// revocation re-activates the existing row instead of colliding with its primary key.</summary>
    internal async Task UpsertMembershipAsync(Membership membership, CancellationToken cancellationToken)
    {
        var current = Current();
        await using var command = current.Connection.CreateCommand();
        command.Transaction = current.Transaction;
        command.CommandText = """
            INSERT INTO organization_membership
                (organization, principal_issuer, principal_subject, role, state, created_at)
            VALUES
                (@organization, @issuer, @subject, @role, @state, @createdAt)
            ON CONFLICT (organization, principal_issuer, principal_subject)
            DO UPDATE SET role = excluded.role, state = excluded.state, created_at = excluded.created_at;
            """;
        AddParameter(command, "@organization", capability.EncodeIdentifier(membership.Organization.Value));
        AddParameter(command, "@issuer", membership.Principal.Issuer);
        AddParameter(command, "@subject", membership.Principal.Subject);
        AddParameter(command, "@role", membership.Role.ToString());
        AddParameter(command, "@state", membership.State.ToString());
        AddParameter(command, "@createdAt", capability.FormatInstant(membership.CreatedAt));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads one membership row, or null when none exists — revoked or never a member alike,
    /// distinguished by <see cref="Membership.State"/> on what is returned.</summary>
    internal async Task<Membership?> FindMembershipAsync(
        OrganizationId organization, PrincipalId principal, CancellationToken cancellationToken)
    {
        var current = Current();
        await using var command = current.Connection.CreateCommand();
        command.Transaction = current.Transaction;
        command.CommandText = """
            SELECT organization, principal_issuer, principal_subject, role, state, created_at
            FROM organization_membership
            WHERE organization = @organization AND principal_issuer = @issuer AND principal_subject = @subject;
            """;
        AddParameter(command, "@organization", capability.EncodeIdentifier(organization.Value));
        AddParameter(command, "@issuer", principal.Issuer);
        AddParameter(command, "@subject", principal.Subject);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadMembership(reader) : null;
    }

    /// <summary>Reads every membership row for one organization, live and revoked alike.</summary>
    internal async Task<IReadOnlyList<Membership>> ListMembershipsAsync(
        OrganizationId organization, CancellationToken cancellationToken)
    {
        var current = Current();
        await using var command = current.Connection.CreateCommand();
        command.Transaction = current.Transaction;
        command.CommandText = """
            SELECT organization, principal_issuer, principal_subject, role, state, created_at
            FROM organization_membership
            WHERE organization = @organization
            ORDER BY created_at;
            """;
        AddParameter(command, "@organization", capability.EncodeIdentifier(organization.Value));

        List<Membership> rows = [];
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(ReadMembership(reader));
        }

        return rows;
    }

    /// <summary>Conditionally revokes one active membership. Returns whether a row was changed —
    /// false when the target was never a member or was already revoked.</summary>
    internal async Task<bool> RevokeMembershipAsync(
        OrganizationId organization, PrincipalId principal, CancellationToken cancellationToken)
    {
        var current = Current();
        await using var command = current.Connection.CreateCommand();
        command.Transaction = current.Transaction;
        command.CommandText = """
            UPDATE organization_membership
            SET state = @revoked
            WHERE organization = @organization AND principal_issuer = @issuer AND principal_subject = @subject
                  AND state = @active;
            """;
        AddParameter(command, "@organization", capability.EncodeIdentifier(organization.Value));
        AddParameter(command, "@issuer", principal.Issuer);
        AddParameter(command, "@subject", principal.Subject);
        AddParameter(command, "@revoked", nameof(MembershipState.Revoked));
        AddParameter(command, "@active", nameof(MembershipState.Active));
        var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return changed == 1;
    }

    /// <summary>Reads the tenant of one organization the given principal actively belongs to, or null
    /// when the organization does not exist or the principal is not an active member — the single
    /// query <c>SwitchActiveOrganizationAsync</c> needs (S10.6, S10.7).</summary>
    internal async Task<TenantId?> FindTenantForActiveMemberAsync(
        OrganizationId organization, PrincipalId principal, CancellationToken cancellationToken)
    {
        var current = Current();
        await using var command = current.Connection.CreateCommand();
        command.Transaction = current.Transaction;
        command.CommandText = """
            SELECT o.tenant
            FROM organization o
            JOIN organization_membership m ON m.organization = o.id
            WHERE o.id = @organization AND m.principal_issuer = @issuer AND m.principal_subject = @subject
                  AND m.state = @active;
            """;
        AddParameter(command, "@organization", capability.EncodeIdentifier(organization.Value));
        AddParameter(command, "@issuer", principal.Issuer);
        AddParameter(command, "@subject", principal.Subject);
        AddParameter(command, "@active", nameof(MembershipState.Active));

        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value is null or DBNull || !TenantId.TryParse((string)value, out var tenant))
        {
            return null;
        }

        return tenant;
    }

    /// <summary>Inserts one invitation row, holding only the token's hash (I-O4).</summary>
    internal async Task InsertInvitationAsync(Invitation invitation, CancellationToken cancellationToken)
    {
        var current = Current();
        await using var command = current.Connection.CreateCommand();
        command.Transaction = current.Transaction;
        command.CommandText = """
            INSERT INTO organization_invitation
                (id, organization, role, expires_at, token_hash, redeemed_by_issuer, redeemed_by_subject, redeemed_at)
            VALUES
                (@id, @organization, @role, @expiresAt, @tokenHash, NULL, NULL, NULL);
            """;
        AddParameter(command, "@id", capability.EncodeIdentifier(invitation.Id.Value));
        AddParameter(command, "@organization", capability.EncodeIdentifier(invitation.Organization.Value));
        AddParameter(command, "@role", invitation.Role.ToString());
        AddParameter(command, "@expiresAt", capability.FormatInstant(invitation.ExpiresAt));
        AddParameter(command, "@tokenHash", invitation.TokenHash);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Conditionally redeems one invitation: the affected row transitions from unredeemed and
    /// unexpired to redeemed in a single statement, so a token presented twice can change at most one
    /// row (I-O3) regardless of how many callers race on it.</summary>
    /// <returns>The organization and role to grant, or null when the conditional update changed no
    /// row — expired, already redeemed, or never existed, indistinguishable from this return alone
    /// (S10.5); the caller logs the specific cause separately via <see cref="DiagnoseAsync"/>.</returns>
    internal async Task<(OrganizationId Organization, OrganizationRole Role)?> TryRedeemInvitationAsync(
        string tokenHash, PrincipalId redeemer, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var current = Current();
        await using (var update = current.Connection.CreateCommand())
        {
            update.Transaction = current.Transaction;
            update.CommandText = """
                UPDATE organization_invitation
                SET redeemed_by_issuer = @issuer, redeemed_by_subject = @subject, redeemed_at = @now
                WHERE token_hash = @tokenHash AND redeemed_at IS NULL AND expires_at > @now;
                """;
            AddParameter(update, "@issuer", redeemer.Issuer);
            AddParameter(update, "@subject", redeemer.Subject);
            AddParameter(update, "@now", capability.FormatInstant(now));
            AddParameter(update, "@tokenHash", tokenHash);
            var changed = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (changed != 1)
            {
                return null;
            }
        }

        await using var read = current.Connection.CreateCommand();
        read.Transaction = current.Transaction;
        read.CommandText = "SELECT organization, role FROM organization_invitation WHERE token_hash = @tokenHash;";
        AddParameter(read, "@tokenHash", tokenHash);
        await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        capability.TryDecodeIdentifier((byte[])reader.GetValue(0), out var organizationId);
        var role = Enum.Parse<OrganizationRole>(reader.GetString(1));
        return (new OrganizationId(organizationId), role);
    }

    /// <summary>Names, for the log only, which of the three causes an unsuccessful redemption had —
    /// never returned to the caller (S10.5).</summary>
    internal async Task<string> DiagnoseAsync(string tokenHash, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var current = Current();
        await using var command = current.Connection.CreateCommand();
        command.Transaction = current.Transaction;
        command.CommandText = "SELECT expires_at, redeemed_at FROM organization_invitation WHERE token_hash = @tokenHash;";
        AddParameter(command, "@tokenHash", tokenHash);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return "never existed";
        }

        if (!reader.IsDBNull(1))
        {
            return "already redeemed";
        }

        return capability.TryParseInstant(reader.GetString(0), out var expiresAt) && expiresAt <= now
            ? "expired"
            : "unknown";
    }

    private IAmbientTransaction Current() =>
        ambient.Current ?? throw new PlatformContractViolationException(ContractViolation.NoAmbientTransaction());

    private Membership ReadMembership(DbDataReader reader)
    {
        capability.TryDecodeIdentifier((byte[])reader.GetValue(0), out var organizationId);
        var principal = new PrincipalId(reader.GetString(1), reader.GetString(2));
        var role = Enum.Parse<OrganizationRole>(reader.GetString(3));
        var state = Enum.Parse<MembershipState>(reader.GetString(4));
        capability.TryParseInstant(reader.GetString(5), out var createdAt);
        return new Membership(new OrganizationId(organizationId), principal, role, state, createdAt);
    }

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}

/// <summary>Creates <c>organization</c>, <c>organization_membership</c> and
/// <c>organization_invitation</c> — three tables, no role-assignment table (I-A9, S10.11): the role
/// is the closed <see cref="OrganizationRole"/> enum, a column on the membership row.</summary>
internal sealed class OrganizationsMigrationSource : IModuleMigrationSource
{
    public ModuleName Module { get; } = new("Organizations");

    public IReadOnlyList<IModuleMigration> Migrations { get; } = [new CreateOrganizationTablesMigration()];
}

internal sealed class CreateOrganizationTablesMigration : IModuleMigration
{
    public string Name => "0001_create_organizations";

    public async Task ApplyAsync(DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken)
    {
        var isSqlite = connection is SqliteConnection;
        var blobType = isSqlite ? "BLOB" : "BYTEA";

        await ExecuteAsync(connection, transaction, $"""
            CREATE TABLE organization (
                id {blobType} NOT NULL,
                tenant TEXT NOT NULL,
                name TEXT NOT NULL,
                owner_issuer TEXT NOT NULL,
                owner_subject TEXT NOT NULL,
                created_at TEXT NOT NULL,
                PRIMARY KEY (id),
                UNIQUE (tenant)
            );
            """, cancellationToken).ConfigureAwait(false);

        // Keyed by issuer and subject as two columns (I-I3, I-O6, S10.10) — never a reference to any
        // user row, which is what lets a Delegated principal hold a membership identically to an
        // Account one. No role-assignment table: role is this closed enum column (I-A9, S10.11).
        await ExecuteAsync(connection, transaction, $"""
            CREATE TABLE organization_membership (
                organization {blobType} NOT NULL,
                principal_issuer TEXT NOT NULL,
                principal_subject TEXT NOT NULL,
                role TEXT NOT NULL,
                state TEXT NOT NULL,
                created_at TEXT NOT NULL,
                PRIMARY KEY (organization, principal_issuer, principal_subject)
            );
            """, cancellationToken).ConfigureAwait(false);

        // The token itself is never stored (I-O4) — only its hash. Redemption is the conditional
        // update TryRedeemInvitationAsync issues against the unredeemed, unexpired state (I-O3).
        await ExecuteAsync(connection, transaction, $"""
            CREATE TABLE organization_invitation (
                id {blobType} NOT NULL,
                organization {blobType} NOT NULL,
                role TEXT NOT NULL,
                expires_at TEXT NOT NULL,
                token_hash TEXT NOT NULL,
                redeemed_by_issuer TEXT NULL,
                redeemed_by_subject TEXT NULL,
                redeemed_at TEXT NULL,
                PRIMARY KEY (id),
                UNIQUE (token_hash),
                CHECK ((redeemed_by_issuer IS NULL) = (redeemed_by_subject IS NULL)),
                CHECK ((redeemed_by_issuer IS NULL) = (redeemed_at IS NULL))
            );
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
