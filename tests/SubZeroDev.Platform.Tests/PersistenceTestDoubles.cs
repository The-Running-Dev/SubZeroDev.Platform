using System.Data.Common;
using Microsoft.Extensions.Logging;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Persistence;

namespace SubZeroDev.Platform.Tests;

/// <summary>Captures every log entry rather than writing anywhere, so a test can assert what a
/// component logged without a real logging provider.</summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    internal List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Entries.Add((logLevel, formatter(state, exception)));
}

/// <summary>An in-memory <see cref="IHostRegistrationStore"/> for testing the two readiness checks
/// and the heartbeat in isolation from real SQL — the store's own upsert semantics (write-once
/// <c>started_at</c>, real reachability failures) are proven against a real database instead, in
/// <c>HostRegistrationTests</c>.</summary>
internal sealed class FakeHostRegistrationStore : IHostRegistrationStore
{
    private readonly Dictionary<(HostRole Role, InstanceId Instance), HostRegistration> _rows = [];

    /// <summary>When set, every method fails as the ordinary retryable outage a missing or
    /// unreachable schema classifies as.</summary>
    internal bool Unavailable { get; set; }

    /// <summary>When set, <see cref="UpsertAsync"/> fails with this instead of the ordinary
    /// <see cref="Unavailable"/> outage — for exercising a write failure that is not the expected
    /// pre-migration case.</summary>
    internal TransactionError? UpsertFailure { get; set; }

    /// <summary>Every registration this store was asked to upsert, in call order — so a test can
    /// assert what a tick wrote without the store's own conflict-resolution policy in the way.</summary>
    internal List<HostRegistration> Upserted { get; } = [];

    internal void Seed(HostRegistration registration) => _rows[(registration.Role, registration.Instance)] = registration;

    public Task<Result<TransactionError>> UpsertAsync(HostRegistration registration, CancellationToken cancellationToken)
    {
        Upserted.Add(registration);

        if (UpsertFailure is { } failure)
        {
            return Task.FromResult(Result<TransactionError>.Failure(failure));
        }

        if (Unavailable)
        {
            return Task.FromResult(Result<TransactionError>.Failure(TransactionError.Unavailable()));
        }

        var key = (registration.Role, registration.Instance);

        // started_at is write-once, mirroring the real store's ON CONFLICT clause: only heartbeat_at
        // changes after the first insert.
        _rows[key] = _rows.TryGetValue(key, out var existing)
            ? registration with { StartedAt = existing.StartedAt }
            : registration;

        return Task.FromResult(Result<TransactionError>.Success());
    }

    public Task<Result<IReadOnlyList<HostRegistration>, TransactionError>> ListLiveAsync(
        DateTimeOffset heartbeatSince, CancellationToken cancellationToken)
    {
        if (Unavailable)
        {
            return Task.FromResult(
                Result<IReadOnlyList<HostRegistration>, TransactionError>.Failure(TransactionError.Unavailable()));
        }

        IReadOnlyList<HostRegistration> live = _rows.Values.Where(row => row.HeartbeatAt >= heartbeatSince).ToList();
        return Task.FromResult(Result<IReadOnlyList<HostRegistration>, TransactionError>.Success(live));
    }

    public Task<Result<TransactionError>> DeleteAsync(HostRole role, InstanceId instance, CancellationToken cancellationToken)
    {
        if (Unavailable)
        {
            return Task.FromResult(Result<TransactionError>.Failure(TransactionError.Unavailable()));
        }

        _rows.Remove((role, instance));
        return Task.FromResult(Result<TransactionError>.Success());
    }
}

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
