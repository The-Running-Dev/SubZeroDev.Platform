using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Persistence;

namespace SubZeroDev.Platform.Sample.Web;

/// <summary>A module with no dependencies. Owns <c>catalogue_items</c> — tenant and audit columns
/// only, no soft delete.</summary>
public sealed class CatalogueModule : IPlatformModule
{
    /// <inheritdoc/>
    public ModuleName Name { get; } = new("Catalogue");

    /// <inheritdoc/>
    public IReadOnlyCollection<ModuleName> DependsOn { get; } = [];

    /// <inheritdoc/>
    public void Register(IServiceCollection services)
    {
        services.AddSingleton<IModuleMigrationSource, CatalogueMigrations>();
    }
}

/// <summary>A module that depends on another, so the sample exercises ordering rather than
/// asserting it only in a unit test. Owns <c>orders</c>, which opts into soft delete.</summary>
public sealed class OrdersModule : IPlatformModule
{
    /// <inheritdoc/>
    public ModuleName Name { get; } = new("Orders");

    /// <inheritdoc/>
    public IReadOnlyCollection<ModuleName> DependsOn { get; } = [new ModuleName("Catalogue")];

    /// <inheritdoc/>
    public void Register(IServiceCollection services)
    {
        services.AddSingleton<IModuleMigrationSource, OrdersMigrations>();
    }
}

internal sealed class CatalogueMigrations : IModuleMigrationSource
{
    public ModuleName Module { get; } = new("Catalogue");

    public IReadOnlyList<IModuleMigration> Migrations { get; } = [new CreateCatalogueItems()];

    private sealed class CreateCatalogueItems : IModuleMigration
    {
        public string Name => "0001_CreateCatalogueItems";

        public async Task ApplyAsync(DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                CREATE TABLE catalogue_items (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    tenant TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    created_by TEXT NULL,
                    modified_at TEXT NULL,
                    modified_by TEXT NULL
                );
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}

internal sealed class OrdersMigrations : IModuleMigrationSource
{
    public ModuleName Module { get; } = new("Orders");

    public IReadOnlyList<IModuleMigration> Migrations { get; } = [new CreateOrders()];

    private sealed class CreateOrders : IModuleMigration
    {
        public string Name => "0001_CreateOrders";

        public async Task ApplyAsync(DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken)
        {
            // No foreign key to catalogue_items: modules relate by holding an identifier and
            // resolving it in application code, never across a module boundary in the schema.
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                CREATE TABLE orders (
                    id TEXT PRIMARY KEY,
                    catalogue_item_id TEXT NOT NULL,
                    quantity INTEGER NOT NULL,
                    tenant TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    created_by TEXT NULL,
                    modified_at TEXT NULL,
                    modified_by TEXT NULL,
                    is_deleted INTEGER NOT NULL DEFAULT 0,
                    deleted_at TEXT NULL,
                    deleted_by TEXT NULL
                );
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
