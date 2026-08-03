using System.Data.Common;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Persistence;

namespace SubZeroDev.Platform.Tests;

/// <summary>A migration whose DDL a test supplies inline.</summary>
internal sealed class TestMigration(string name, Func<DbConnection, DbTransaction, CancellationToken, Task> apply)
    : IModuleMigration
{
    public string Name { get; } = name;

    public Task ApplyAsync(DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken) =>
        apply(connection, transaction, cancellationToken);

    /// <summary>A migration that runs one DDL statement with no parameters.</summary>
    internal static TestMigration Sql(string name, string sql) =>
        new(name, async (connection, transaction, cancellationToken) =>
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        });
}

/// <summary>A module's migration contribution, named and populated by a test.</summary>
internal sealed class TestMigrationSource(string module, params IModuleMigration[] migrations) : IModuleMigrationSource
{
    public ModuleName Module { get; } = new(module);

    public IReadOnlyList<IModuleMigration> Migrations { get; } = migrations;
}

/// <summary>Orders two byte arrays lexicographically, the same comparison a database performs over
/// a blob column.</summary>
internal sealed class ByteArrayComparer : IComparer<byte[]>
{
    internal static readonly ByteArrayComparer Instance = new();

    public int Compare(byte[]? x, byte[]? y)
    {
        ArgumentNullException.ThrowIfNull(x);
        ArgumentNullException.ThrowIfNull(y);

        var length = Math.Min(x.Length, y.Length);
        for (var index = 0; index < length; index++)
        {
            var comparison = x[index].CompareTo(y[index]);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return x.Length.CompareTo(y.Length);
    }
}
