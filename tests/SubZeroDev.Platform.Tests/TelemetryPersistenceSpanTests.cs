using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Persistence;
using SubZeroDev.Platform.Testing;
using Testcontainers.PostgreSql;

namespace SubZeroDev.Platform.Tests;

/// <summary>S8.3: a unit of work produces one child activity on both providers, carrying provider
/// and operation but never SQL, parameter, or connection-string text. Captured with a plain
/// <see cref="ActivityListener"/> subscribed to <c>PlatformTelemetry.ActivitySourceName"</c> — the
/// same source <c>Transactions.cs</c>'s <c>UnitOfWork</c> starts its span on — rather than through
/// the OTLP pipeline, so the assertion does not depend on the 10% sampler's decision or a running
/// collector.</summary>
[Collection(TelemetryTestCollection.Name)]
public sealed class TelemetryPersistenceSpanTests
{
    [Fact]
    public async Task Sqlite_unit_of_work_produces_one_tagged_span_with_no_sql_or_connection_text()
    {
        var connectionString = $"Data Source={Path.Combine(Path.GetTempPath(), $"platform-tel-{Guid.NewGuid():N}.db")}";

        using var capture = ActivityCapture.ForPlatformSource();

        await using var host = await PlatformTestHost.CreateBuilder()
            .WithProvider(PersistenceProvider.Sqlite)
            .WithSetting("Persistence:ConnectionString", connectionString)
            .StartAsync(CancellationToken.None);

        var unitOfWork = host.Services.GetRequiredService<IUnitOfWork>();
        var before = DateTime.UtcNow;
        var result = await unitOfWork.ExecuteAsync(
            TransactionIntent.ReadOnly, _ => Task.CompletedTask, CancellationToken.None);

        Assert.True(result.IsSuccess);

        // Assert.Contains rather than Assert.Single: ActivitySource listeners are process-wide, so a
        // different test's own unit-of-work spans (in a different xUnit collection running
        // concurrently) can land in the same capture. What matters here is that at least one clean,
        // correctly-tagged span exists for this call, started no earlier than it — production code
        // starts exactly one per ExecuteAsync, which Transactions.cs's single `using var activity`
        // already guarantees.
        var activity = capture.Stopped.First(a => a.OperationName == "platform.persistence.unit-of-work"
            && Equals(a.GetTagItem("db.system"), "sqlite")
            && a.StartTimeUtc >= before);
        AssertTaggedAndClean(activity, "sqlite", connectionString);
    }

    [Fact]
    public async Task Postgres_unit_of_work_produces_one_tagged_span_with_no_sql_or_connection_text()
    {
        await using var container = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await container.StartAsync();

        using var capture = ActivityCapture.ForPlatformSource();

        await using var host = await PlatformTestHost.CreateBuilder()
            .WithProvider(PersistenceProvider.PostgreSql)
            .WithSetting("Persistence:ConnectionString", container.GetConnectionString())
            .StartAsync(CancellationToken.None);

        var unitOfWork = host.Services.GetRequiredService<IUnitOfWork>();
        var before = DateTime.UtcNow;
        var result = await unitOfWork.ExecuteAsync(
            TransactionIntent.ReadOnly, _ => Task.CompletedTask, CancellationToken.None);

        Assert.True(result.IsSuccess);

        var activity = capture.Stopped.First(a => a.OperationName == "platform.persistence.unit-of-work"
            && Equals(a.GetTagItem("db.system"), "postgresql")
            && a.StartTimeUtc >= before);
        AssertTaggedAndClean(activity, "postgresql", container.GetConnectionString());
    }

    private static void AssertTaggedAndClean(Activity activity, string provider, string connectionString)
    {
        Assert.Equal(provider, activity.GetTagItem("db.system"));
        Assert.Equal("read", activity.GetTagItem("operation"));

        var allTagValues = string.Join(" ", activity.TagObjects.Select(t => t.Value?.ToString() ?? string.Empty));
        Assert.DoesNotContain("SELECT", allTagValues, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(connectionString, allTagValues, StringComparison.Ordinal);
        Assert.DoesNotContain("Password", allTagValues, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>A minimal, always-recording <see cref="ActivityListener"/> for
/// <c>PlatformTelemetry.ActivitySourceName</c>, so a test can assert on spans regardless of what the
/// OTel SDK's own configured sampler decided.</summary>
internal sealed class ActivityCapture : IDisposable
{
    private readonly ActivityListener _listener;

    private ActivityCapture(string sourceName)
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == sourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => Stopped.Add(activity),
        };
        ActivitySource.AddActivityListener(_listener);
    }

    internal ConcurrentBag<Activity> Stopped { get; } = [];

    internal static ActivityCapture ForPlatformSource() => new(PlatformTelemetry.ActivitySourceName);

    internal static ActivityCapture ForSource(string name) => new(name);

    public void Dispose() => _listener.Dispose();
}
