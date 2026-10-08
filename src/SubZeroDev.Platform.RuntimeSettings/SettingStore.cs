using System.Data.Common;
using System.Text;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Persistence;

namespace SubZeroDev.Platform.RuntimeSettings;

/// <summary>Reads <c>runtime_setting</c> rows. Enlists against the ambient transaction the reader
/// opened through <see cref="IUnitOfWork"/>, never a connection of its own, and holds no copy of what
/// it read (I-ST7).</summary>
internal sealed class SettingStore(IAmbientTransactionAccessor ambient)
{
    /// <summary>The stored text of each layer, in one statement. A branch for a layer that does not
    /// exist is omitted, and tenant and user rows are read only with the current tenant (I-ST5).</summary>
    internal async Task<IReadOnlyDictionary<SettingLayer, string>> ReadAsync(
        SettingName name,
        IReadOnlySet<SettingLayer> layers,
        TenantId tenant,
        PrincipalId principal,
        CancellationToken cancellationToken)
    {
        var current = ambient.Current
            ?? throw new PlatformContractViolationException(ContractViolation.NoAmbientTransaction());
        await using var command = current.Connection.CreateCommand();
        command.Transaction = current.Transaction;

        var branches = new List<string>();
        if (layers.Contains(SettingLayer.Global))
        {
            branches.Add("(layer = 'global')");
        }

        if (layers.Contains(SettingLayer.Tenant))
        {
            branches.Add("(layer = 'tenant' AND tenant = @current)");
        }

        if (layers.Contains(SettingLayer.User))
        {
            branches.Add(
                "(layer = 'user' AND tenant = @current AND principal_issuer = @issuer AND principal_subject = @subject)");
            AddParameter(command, "@issuer", principal.Issuer);
            AddParameter(command, "@subject", principal.Subject);
        }

        if (layers.Contains(SettingLayer.Tenant) || layers.Contains(SettingLayer.User))
        {
            AddParameter(command, "@current", tenant.ToString());
        }

        command.CommandText = new StringBuilder("SELECT layer, value FROM runtime_setting WHERE name = @name AND (")
            .AppendJoin(" OR ", branches)
            .Append(");")
            .ToString();
        AddParameter(command, "@name", name.Value);

        var rows = new Dictionary<SettingLayer, string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (ParseLayer(reader.GetString(0)) is { } layer)
            {
                rows[layer] = reader.GetString(1);
            }
        }

        return rows;
    }

    private static SettingLayer? ParseLayer(string stored) => stored switch
    {
        "global" => SettingLayer.Global,
        "tenant" => SettingLayer.Tenant,
        "user" => SettingLayer.User,
        _ => null,
    };

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}

/// <summary>Creates <c>runtime_setting</c>, the module's one table, with the same text on SQLite and
/// PostgreSQL (<c>design/20-contract.md</c>, Schemas §9). No existing migration changes (I-T7).</summary>
internal sealed class RuntimeSettingsMigrationSource : IModuleMigrationSource
{
    public ModuleName Module { get; } = new("RuntimeSettings");

    public IReadOnlyList<IModuleMigration> Migrations { get; } = [new CreateRuntimeSettingsMigration()];
}

internal sealed class CreateRuntimeSettingsMigration : IModuleMigration
{
    public string Name => "0001_create_runtime_settings";

    public async Task ApplyAsync(DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken)
    {
        // '' marks "no principal" unambiguously: neither half of a PrincipalId is ever empty. A global
        // row is the installation's and lives under the all-zero tenant (I-ST5).
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE runtime_setting (
                tenant TEXT NOT NULL,
                layer TEXT NOT NULL,
                name TEXT NOT NULL,
                principal_issuer TEXT NOT NULL,
                principal_subject TEXT NOT NULL,
                value TEXT NOT NULL,
                PRIMARY KEY (tenant, layer, name, principal_issuer, principal_subject),
                CHECK (layer IN ('global', 'tenant', 'user')),
                CHECK ((layer = 'user') = (principal_issuer <> '')),
                CHECK ((layer = 'user') = (principal_subject <> '')),
                CHECK (layer <> 'global' OR tenant = '00000000-0000-0000-0000-000000000000')
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
