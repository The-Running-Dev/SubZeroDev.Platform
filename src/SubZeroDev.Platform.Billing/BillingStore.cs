using System.Data.Common;
using Microsoft.Data.Sqlite;
using Npgsql;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Persistence;

namespace SubZeroDev.Platform.Billing;

/// <summary>Stores plan, subscription and provider-event-receipt rows. Every member enlists against
/// the ambient transaction the caller already opened via <see cref="IUnitOfWork"/> — never a
/// connection of its own — the same discipline <c>OrganizationStore</c> follows (S10.1).</summary>
internal sealed class BillingStore(IAmbientTransactionAccessor ambient, IProviderCapability capability)
{
    /// <summary>Whether an exception raised while writing is a unique-constraint violation — the
    /// subscription table's unique tenant index (S11.8) or the receipt table's unique key (I-B7).
    /// The caller inspects a flag it set from the matching <c>catch when</c> after the exception has
    /// unwound through <see cref="IUnitOfWork.ExecuteAsync{T}"/> and rolled the whole transaction
    /// back the normal way — swallowing it here would leave a PostgreSQL transaction aborted for
    /// every statement that follows (see <c>OrganizationStore.InsertOrganizationAsync</c>'s
    /// remarks for the identical reasoning).</summary>
    internal static bool IsUniqueViolation(Exception exception) => exception switch
    {
        SqliteException sqlite => sqlite.SqliteErrorCode == 19, // SQLITE_CONSTRAINT
        PostgresException postgres => postgres.SqlState == "23505", // unique_violation
        _ => false,
    };

    // Plan --------------------------------------------------------------------------------------

    /// <summary>Replaces the rows for one plan's key with the given display name and feature set. A
    /// plan is stored as one row per feature plus a sentinel row (empty feature name) so a plan with
    /// no features still exists as a row (S11.2's "no per-feature fan-out" is about entitlement, not
    /// this: a plan definition write is not on the request path).</summary>
    internal async Task UpsertPlanAsync(Plan plan, CancellationToken cancellationToken)
    {
        var current = Current();

        await using (var delete = current.Connection.CreateCommand())
        {
            delete.Transaction = current.Transaction;
            delete.CommandText = "DELETE FROM plan WHERE plan_key = @key;";
            AddParameter(delete, "@key", plan.Key.Value);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var featureNames = plan.Features.Select(f => f.Value).Prepend(string.Empty);
        foreach (var featureName in featureNames.Distinct(StringComparer.Ordinal))
        {
            await using var insert = current.Connection.CreateCommand();
            insert.Transaction = current.Transaction;
            insert.CommandText = """
                INSERT INTO plan (plan_key, display_name, feature_name)
                VALUES (@key, @displayName, @featureName);
                """;
            AddParameter(insert, "@key", plan.Key.Value);
            AddParameter(insert, "@displayName", plan.DisplayName);
            AddParameter(insert, "@featureName", featureName);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Reads one plan back, or null when its key is not registered.</summary>
    internal async Task<Plan?> FindPlanAsync(PlanKey key, CancellationToken cancellationToken)
    {
        var current = Current();
        await using var command = current.Connection.CreateCommand();
        command.Transaction = current.Transaction;
        command.CommandText = "SELECT display_name, feature_name FROM plan WHERE plan_key = @key;";
        AddParameter(command, "@key", key.Value);

        string? displayName = null;
        var features = new HashSet<FeatureName>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            displayName ??= reader.GetString(0);
            var featureName = reader.GetString(1);
            if (featureName.Length > 0)
            {
                features.Add(new FeatureName(featureName));
            }
        }

        return displayName is null ? null : new Plan(key, displayName, features);
    }

    /// <summary>Whether <paramref name="key"/> grants <paramref name="feature"/> — one indexed
    /// lookup, never a parse of every plan's feature set (S11.2).</summary>
    internal async Task<bool> PlanGrantsFeatureAsync(
        PlanKey key, FeatureName feature, CancellationToken cancellationToken)
    {
        var current = Current();
        await using var command = current.Connection.CreateCommand();
        command.Transaction = current.Transaction;
        command.CommandText =
            "SELECT 1 FROM plan WHERE plan_key = @key AND feature_name = @feature LIMIT 1;";
        AddParameter(command, "@key", key.Value);
        AddParameter(command, "@feature", feature.Value);

        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is not null and not DBNull;
    }

    // Subscription --------------------------------------------------------------------------------

    /// <summary>Reads one tenant's subscription, or null when it has none.</summary>
    internal async Task<Subscription?> FindSubscriptionAsync(TenantId tenant, CancellationToken cancellationToken)
    {
        var current = Current();
        await using var command = current.Connection.CreateCommand();
        command.Transaction = current.Transaction;
        command.CommandText = """
            SELECT id, tenant, plan_key, state, period_start, period_end, trial_ends_at, provider_reference
            FROM subscription
            WHERE tenant = @tenant;
            """;
        AddParameter(command, "@tenant", tenant.ToString());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadSubscription(reader) : null;
    }

    /// <summary>Inserts a brand-new subscription row — the tenant's first. The unique index on
    /// <c>tenant</c> is what turns a concurrent double-insert into a constraint violation rather
    /// than a silent second row (S11.8).</summary>
    internal async Task InsertSubscriptionAsync(Subscription subscription, CancellationToken cancellationToken)
    {
        var current = Current();
        await using var command = current.Connection.CreateCommand();
        command.Transaction = current.Transaction;
        command.CommandText = """
            INSERT INTO subscription
                (id, tenant, plan_key, state, period_start, period_end, trial_ends_at, provider_reference)
            VALUES
                (@id, @tenant, @plan, @state, @periodStart, @periodEnd, @trialEndsAt, @providerReference);
            """;
        BindSubscription(command, subscription);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Updates the tenant's existing subscription row in place — one row, no per-feature
    /// fan-out (S11.2).</summary>
    internal async Task UpdateSubscriptionAsync(Subscription subscription, CancellationToken cancellationToken)
    {
        var current = Current();
        await using var command = current.Connection.CreateCommand();
        command.Transaction = current.Transaction;
        command.CommandText = """
            UPDATE subscription
            SET plan_key = @plan, state = @state, period_start = @periodStart, period_end = @periodEnd,
                trial_ends_at = @trialEndsAt, provider_reference = @providerReference
            WHERE tenant = @tenant;
            """;
        BindSubscription(command, subscription);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Upserts a subscription from provider-authoritative state: an inbound event is never
    /// checked against the transition adjacency an administrative call is (a provider reports what
    /// is true; Billing does not second-guess it).</summary>
    internal async Task<bool> UpsertSubscriptionFromProviderAsync(
        Subscription subscription, CancellationToken cancellationToken)
    {
        var existing = await FindSubscriptionAsync(subscription.Tenant, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            await InsertSubscriptionAsync(subscription, cancellationToken).ConfigureAwait(false);
            return true;
        }

        await UpdateSubscriptionAsync(subscription with { Id = existing.Id }, cancellationToken).ConfigureAwait(false);
        return true;
    }

    // Provider event receipt ----------------------------------------------------------------------

    /// <summary>Inserts one receipt. The primary key on (provider, provider event id) is the unique
    /// receipt key I-B7 relies on — a redelivery violates it, caught by
    /// <see cref="IsUniqueViolation"/>.</summary>
    internal async Task InsertReceiptAsync(ProviderEventReceipt receipt, CancellationToken cancellationToken)
    {
        var current = Current();
        await using var command = current.Connection.CreateCommand();
        command.Transaction = current.Transaction;
        command.CommandText = """
            INSERT INTO provider_event_receipt (provider, provider_event_id, received_at)
            VALUES (@provider, @providerEventId, @receivedAt);
            """;
        AddParameter(command, "@provider", receipt.Provider);
        AddParameter(command, "@providerEventId", receipt.ProviderEventId);
        AddParameter(command, "@receivedAt", capability.FormatInstant(receipt.ReceivedAt));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private void BindSubscription(DbCommand command, Subscription subscription)
    {
        AddParameter(command, "@id", capability.EncodeIdentifier(subscription.Id.Value));
        AddParameter(command, "@tenant", subscription.Tenant.ToString());
        AddParameter(command, "@plan", subscription.Plan.Value);
        AddParameter(command, "@state", subscription.State.ToString());
        AddParameter(command, "@periodStart", capability.FormatInstant(subscription.PeriodStart));
        AddParameter(command, "@periodEnd", capability.FormatInstant(subscription.PeriodEnd));
        AddParameter(
            command,
            "@trialEndsAt",
            subscription.TrialEndsAt is { } trialEndsAt ? capability.FormatInstant(trialEndsAt) : null);
        AddParameter(command, "@providerReference", subscription.ProviderReference);
    }

    private Subscription ReadSubscription(DbDataReader reader)
    {
        capability.TryDecodeIdentifier((byte[])reader.GetValue(0), out var id);
        TenantId.TryParse(reader.GetString(1), out var tenant);
        var plan = new PlanKey(reader.GetString(2));
        var state = Enum.Parse<SubscriptionState>(reader.GetString(3));
        capability.TryParseInstant(reader.GetString(4), out var periodStart);
        capability.TryParseInstant(reader.GetString(5), out var periodEnd);
        DateTimeOffset? trialEndsAt = reader.IsDBNull(6)
            ? null
            : capability.TryParseInstant(reader.GetString(6), out var parsed) ? parsed : null;
        var providerReference = reader.GetString(7);

        return new Subscription(
            new SubscriptionId(id), tenant, plan, state, periodStart, periodEnd, trialEndsAt, providerReference);
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

/// <summary>Creates <c>plan</c>, <c>subscription</c> and <c>provider_event_receipt</c> — the three
/// tables <c>design/20-contract.md</c>'s Persisted schemas §3 names. Nothing here stores an
/// entitlement (I-B4): a plan transition is one row changing, never a fan-out.</summary>
internal sealed class BillingMigrationSource : IModuleMigrationSource
{
    public ModuleName Module { get; } = new("Billing");

    public IReadOnlyList<IModuleMigration> Migrations { get; } = [new CreateBillingTablesMigration()];
}

internal sealed class CreateBillingTablesMigration : IModuleMigration
{
    public string Name => "0001_create_billing";

    public async Task ApplyAsync(DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken)
    {
        var isSqlite = connection is SqliteConnection;
        var blobType = isSqlite ? "BLOB" : "BYTEA";

        // A plan's feature set is stored as its own rows, not a serialised blob (design/20-contract.md,
        // Persisted schemas §3): a feature name is found with one indexed lookup rather than parsing
        // every plan. The empty-string feature name is the sentinel row that lets a plan with zero
        // features still exist as a row.
        await ExecuteAsync(connection, transaction, """
            CREATE TABLE plan (
                plan_key TEXT NOT NULL,
                display_name TEXT NOT NULL,
                feature_name TEXT NOT NULL,
                PRIMARY KEY (plan_key, feature_name)
            );
            """, cancellationToken).ConfigureAwait(false);

        // Unique on tenant: one tenant holds at most one subscription, enforced by the store rather
        // than by a check in code (I-O8, S11.8). No entitlement column exists here — entitlement is
        // derived from plan_key, state and the clock (I-B4).
        await ExecuteAsync(connection, transaction, $"""
            CREATE TABLE subscription (
                id {blobType} NOT NULL,
                tenant TEXT NOT NULL,
                plan_key TEXT NOT NULL,
                state TEXT NOT NULL,
                period_start TEXT NOT NULL,
                period_end TEXT NOT NULL,
                trial_ends_at TEXT NULL,
                provider_reference TEXT NOT NULL,
                PRIMARY KEY (id),
                UNIQUE (tenant)
            );
            """, cancellationToken).ConfigureAwait(false);

        // The primary key is the unique receipt key I-B7 relies on: a redelivered event violates it,
        // which is what makes a redelivery idempotent by the store rather than by a check.
        await ExecuteAsync(connection, transaction, """
            CREATE TABLE provider_event_receipt (
                provider TEXT NOT NULL,
                provider_event_id TEXT NOT NULL,
                received_at TEXT NOT NULL,
                PRIMARY KEY (provider, provider_event_id)
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
