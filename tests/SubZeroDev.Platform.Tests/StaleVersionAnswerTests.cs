using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Hosting;
using SubZeroDev.Platform.Mcp;
using SubZeroDev.Platform.Persistence;

namespace SubZeroDev.Platform.Tests;

/// <summary>S35.6: how a host answers <see cref="TransactionError.StaleVersion"/> over HTTP and MCP.
/// Platform ships no mapper for <see cref="TransactionError"/>; the host maps it, and these tests
/// are that host. The row is at a version neither answer may echo — the stored and the expected
/// version are both chosen to be digit strings no envelope could contain by accident.</summary>
public sealed class StaleVersionAnswerTests : IDisposable
{
    private const long StoredVersion = 1234567;
    private const long ExpectedVersion = 7654321;
    private const string McpSentence = "The resource changed since it was read; read it again before repeating the call.";

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"platform-stale-{Guid.NewGuid():N}.db");

    public StaleVersionAnswerTests()
    {
        // The startup check refuses a SQLite file not in WAL mode; an operator's file already is.
        using var connection = new SqliteConnection($"Data Source={_path}");
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=wal;";
        pragma.ExecuteNonQuery();
    }

    private Dictionary<string, string?> Settings => new()
    {
        ["Platform:Persistence:ConnectionString"] = $"Data Source={_path}",
    };

    [Fact]
    public async Task S35_6_An_endpoint_answers_a_stale_write_with_409_carrying_only_the_code_and_correlation()
    {
        var (app, client) = await WebHostUnderTest.StartAsync(
            services => services.AddPlatformPersistence(),
            settings: Settings,
            mapEndpoints: application => application.MapPost(
                    "/versioned",
                    async (IUnitOfWork unitOfWork, IVersionGuard guard, IAmbientTransactionAccessor ambient,
                        ICurrentCorrelation correlation, HttpContext http) =>
                    {
                        var executed = await unitOfWork.ExecuteAsync(
                            TransactionIntent.Write,
                            token => StaleWriteAsync(ambient, guard, token),
                            http.RequestAborted);
                        if (executed.IsSuccess)
                        {
                            return Results.NoContent();
                        }

                        var envelope = new ErrorEnvelope(executed.Error.Code, correlation.Current);
                        return executed.Error.Code == nameof(TransactionError.StaleVersion)
                            ? Results.Json(new { code = envelope.Code, correlation = envelope.Correlation.TraceId }, statusCode: 409)
                            : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                    })
                .ExemptFromPlatformAuthorization("S35.6 test endpoint mapping a stale guarded write; not a product surface."));

        try
        {
            await CreateVersionedRowAsync(app.Services);

            var response = await client.PostAsync("/versioned", content: null, CancellationToken.None);
            var raw = await response.Content.ReadAsStringAsync(CancellationToken.None);
            var body = JsonSerializer.Deserialize<JsonElement>(raw);

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal(["code", "correlation"], body.EnumerateObject().Select(p => p.Name).ToArray());
            Assert.Equal("StaleVersion", body.GetProperty("code").GetString());
            Assert.Equal(32, body.GetProperty("correlation").GetString()!.Length);
            AssertNamesNoVersion(raw);
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task S35_6_A_tool_answers_a_stale_write_with_one_fixed_sentence()
    {
        IServiceProvider? services = null;
        var invoker = new McpInvocationTests.StubToolInvoker(
            "producer",
            async (_, _, token) =>
            {
                var executed = await services!.GetRequiredService<IUnitOfWork>().ExecuteAsync(
                    TransactionIntent.Write,
                    inner => StaleWriteAsync(
                        services!.GetRequiredService<IAmbientTransactionAccessor>(),
                        services!.GetRequiredService<IVersionGuard>(),
                        inner),
                    token);
                if (executed.IsSuccess)
                {
                    return ToolInvocationResult.Success("written");
                }

                return executed.Error.Code == nameof(TransactionError.StaleVersion)
                    ? ToolInvocationResult.Failure(McpSentence)
                    : ToolInvocationResult.Failure("The store is unavailable.");
            });
        await using var harness = await McpInvocationTests.Harness.StartAsync(
            exposed: [new ToolName("rename")],
            extra: [
                new StubToolProducer("producer",
                    new ToolDefinition(new ToolName("rename"), "Renames.", McpInvocationTests.Schema(), McpInvocationTests.UsePermission, null)),
                invoker,
                (Action<IServiceCollection>)(collection => collection.AddPlatformPersistence()),
            ],
            settings: Settings);
        services = harness.Services;
        await CreateVersionedRowAsync(services);

        await using var client = await harness.ConnectAsync("alice");
        var result = await client.CallToolAsync(
            "rename", new Dictionary<string, object?>(), cancellationToken: CancellationToken.None);

        Assert.True(result.IsError);
        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        Assert.Equal(McpSentence, text);
        AssertNamesNoVersion(JsonSerializer.Serialize(result));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            File.Delete(_path + suffix);
        }
    }

    private static void AssertNamesNoVersion(string answer)
    {
        Assert.DoesNotContain(StoredVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), answer, StringComparison.Ordinal);
        Assert.DoesNotContain(ExpectedVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), answer, StringComparison.Ordinal);
    }

    private static async Task CreateVersionedRowAsync(IServiceProvider services)
    {
        var ambient = services.GetRequiredService<IAmbientTransactionAccessor>();
        var created = await services.GetRequiredService<IUnitOfWork>().ExecuteAsync(
            TransactionIntent.Write,
            async token =>
            {
                foreach (var sql in new[]
                {
                    "CREATE TABLE t_versioned (id TEXT PRIMARY KEY, tenant TEXT NOT NULL, name TEXT NOT NULL, version INTEGER NOT NULL DEFAULT 1);",
                    $"INSERT INTO t_versioned (id, tenant, name, version) VALUES ('row', '{TenantId.Implicit}', 'original', {StoredVersion});",
                })
                {
                    await using var command = ambient.Current!.Connection.CreateCommand();
                    command.Transaction = ambient.Current.Transaction;
                    command.CommandText = sql;
                    await command.ExecuteNonQueryAsync(token);
                }
            },
            CancellationToken.None);
        Assert.True(created.IsSuccess);
    }

    /// <summary>Asserts a version the row is not at, so the guard always finds it stale.</summary>
    private static async Task<Result<TransactionError>> StaleWriteAsync(
        IAmbientTransactionAccessor ambient, IVersionGuard guard, CancellationToken token)
    {
        await using var update = ambient.Current!.Connection.CreateCommand();
        update.CommandText =
            "UPDATE t_versioned SET name = 'renamed', version = @expected + 1 "
            + "WHERE id = 'row' AND tenant = @tenant AND version = @expected;";
        var expected = update.CreateParameter();
        expected.ParameterName = "@expected";
        expected.Value = ExpectedVersion;
        update.Parameters.Add(expected);
        var tenant = update.CreateParameter();
        tenant.ParameterName = "@tenant";
        tenant.Value = TenantId.Implicit.ToString();
        update.Parameters.Add(tenant);
        return await guard.ExecuteAsync(update, token);
    }
}
