using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Hosting;
using SubZeroDev.Platform.Persistence;
using SubZeroDev.Platform.RuntimeSettings;
using SubZeroDev.Platform.Testing;

namespace SubZeroDev.Platform.Tests;

/// <summary>S43: a product declares its runtime settings once and reads the value that applies to
/// whoever is asking — layer precedence, the startup checks on the catalogue, and the reader's errors,
/// on SQLite. <c>RuntimeSettingsPostgresTests</c> repeats S43.8 on PostgreSQL.</summary>
public sealed class RuntimeSettingsTests
{
    internal static readonly TenantId T1 = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    internal static readonly TenantId T2 = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));

    internal static readonly Principal AccountA = FakePrincipals.Account("issuer-a", "account-a");

    internal static readonly SettingName MaxConcurrentName = new("Sample.Runs.MaxConcurrent");

    internal static readonly IReadOnlySet<SettingLayer> AllLayers =
        new HashSet<SettingLayer> { SettingLayer.Global, SettingLayer.Tenant, SettingLayer.User };

    internal static readonly SettingDefinition<long> MaxConcurrent =
        SettingDefinition.Integer(MaxConcurrentName, 4, AllLayers);

    // S43.1 -----------------------------------------------------------------------------------

    [Fact]
    public async Task S43_1_With_no_rows_stored_a_read_returns_the_default_with_no_layer()
    {
        await using var host = await StartHostAsync(new SampleCatalog(MaxConcurrent));

        var read = await ReadAsync(host, MaxConcurrent, T1, AccountA);

        Assert.True(read.IsSuccess);
        Assert.Equal(new ResolvedSetting<long>(4, null), read.Value);
    }

    // S43.2 -----------------------------------------------------------------------------------

    [Fact]
    public async Task S43_2_Account_A_in_T1_reads_its_own_user_value()
    {
        await using var host = await StartHostWithSampleRowsAsync(MaxConcurrent);

        var read = await ReadAsync(host, MaxConcurrent, T1, AccountA);

        Assert.Equal(new ResolvedSetting<long>(2, SettingLayer.User), read.Value);
    }

    [Fact]
    public async Task S43_2_Account_A_in_T2_reads_the_global_value_because_its_user_row_belongs_to_T1()
    {
        await using var host = await StartHostWithSampleRowsAsync(MaxConcurrent);

        var read = await ReadAsync(host, MaxConcurrent, T2, AccountA);

        Assert.Equal(new ResolvedSetting<long>(8, SettingLayer.Global), read.Value);
    }

    public static TheoryData<string> NonAccountPrincipals => ["delegated", "system", "anonymous"];

    [Theory]
    [MemberData(nameof(NonAccountPrincipals))]
    public async Task S43_2_A_principal_that_is_not_an_account_in_T1_reads_the_tenant_value(string kind)
    {
        await using var host = await StartHostWithSampleRowsAsync(MaxConcurrent);

        // The delegated principal carries account A's own issuer and subject: only its kind keeps
        // the user row from answering.
        var principal = kind switch
        {
            "delegated" => FakePrincipals.Delegated(AccountA.Id.Issuer, AccountA.Id.Subject),
            "system" => FakePrincipals.System,
            _ => FakePrincipals.Anonymous,
        };

        var read = await ReadAsync(host, MaxConcurrent, T1, principal);

        Assert.Equal(new ResolvedSetting<long>(6, SettingLayer.Tenant), read.Value);
    }

    [Theory]
    [MemberData(nameof(NonAccountPrincipals))]
    [InlineData("account")]
    public async Task S43_2_Any_principal_under_the_implicit_tenant_reads_the_global_value(string kind)
    {
        await using var host = await StartHostWithSampleRowsAsync(MaxConcurrent);

        // A user and a tenant row stored under the implicit tenant itself must not answer either:
        // neither layer exists there.
        await InsertAsync(host, TenantId.Implicit, "tenant", MaxConcurrentName.Value, "", "", "5");
        await InsertAsync(host, TenantId.Implicit, "user", MaxConcurrentName.Value, AccountA.Id.Issuer, AccountA.Id.Subject, "3");

        var principal = kind switch
        {
            "account" => AccountA,
            "delegated" => FakePrincipals.Delegated(),
            "system" => FakePrincipals.System,
            _ => FakePrincipals.Anonymous,
        };

        var read = await ReadAsync(host, MaxConcurrent, TenantId.Implicit, principal);

        Assert.Equal(new ResolvedSetting<long>(8, SettingLayer.Global), read.Value);
    }

    [Fact]
    public async Task S43_2_A_row_stored_after_a_read_answers_the_next_read_because_nothing_is_held_in_process()
    {
        await using var host = await StartHostAsync(new SampleCatalog(MaxConcurrent));
        Assert.Equal(new ResolvedSetting<long>(4, null), (await ReadAsync(host, MaxConcurrent, T1, AccountA)).Value);

        await InsertAsync(host, T1, "tenant", MaxConcurrentName.Value, "", "", "6");

        Assert.Equal(new ResolvedSetting<long>(6, SettingLayer.Tenant), (await ReadAsync(host, MaxConcurrent, T1, AccountA)).Value);
    }

    // S43.3 -----------------------------------------------------------------------------------

    [Fact]
    public async Task S43_3_A_declaration_admitting_only_global_and_tenant_ignores_the_user_row()
    {
        var withoutUser = SettingDefinition.Integer(
            MaxConcurrentName, 4, new HashSet<SettingLayer> { SettingLayer.Global, SettingLayer.Tenant });
        await using var host = await StartHostWithSampleRowsAsync(withoutUser);

        var read = await ReadAsync(host, withoutUser, T1, AccountA);

        Assert.Equal(new ResolvedSetting<long>(6, SettingLayer.Tenant), read.Value);
    }

    [Fact]
    public async Task S43_3_A_declaration_admitting_only_global_ignores_the_tenant_and_user_rows()
    {
        var globalOnly = SettingDefinition.Integer(MaxConcurrentName, 4, new HashSet<SettingLayer> { SettingLayer.Global });
        await using var host = await StartHostWithSampleRowsAsync(globalOnly);

        var read = await ReadAsync(host, globalOnly, T1, AccountA);

        Assert.Equal(new ResolvedSetting<long>(8, SettingLayer.Global), read.Value);
    }

    [Fact]
    public async Task S43_3_A_user_only_declaration_read_where_no_user_layer_exists_returns_the_default_without_reading()
    {
        var userOnly = SettingDefinition.Integer(MaxConcurrentName, 4, new HashSet<SettingLayer> { SettingLayer.User });

        // No migration: a query would fail with StoreUnavailable rather than answer the default.
        await using var host = await StartHostAsync(new SampleCatalog(userOnly), migrate: false);

        var read = await ReadAsync(host, userOnly, T1, FakePrincipals.System);

        Assert.True(read.IsSuccess);
        Assert.Equal(new ResolvedSetting<long>(4, null), read.Value);
    }

    // S43.4 -----------------------------------------------------------------------------------

    public static TheoryData<string, string> CatalogueDefects => new()
    {
        { "duplicate", nameof(SettingCatalogError.DuplicateSettingName) },
        { "invalid-default", nameof(SettingCatalogError.InvalidSettingDefault) },
        { "no-layers", nameof(SettingCatalogError.SettingWithoutLayers) },
        { "sensitive", nameof(SettingCatalogError.SensitiveSettingName) },
    };

    [Theory]
    [MemberData(nameof(CatalogueDefects))]
    public async Task S43_4_A_defective_catalogue_fails_host_start_and_the_host_never_serves(string defect, string expected)
    {
        var atMostTen = (Func<long, bool>)(value => value <= 10);
        ISettingCatalog[] catalogs = defect switch
        {
            "duplicate" => [new SampleCatalog(MaxConcurrent), new SampleCatalog(SettingDefinition.Integer(MaxConcurrentName, 2, AllLayers))],
            "invalid-default" => [new SampleCatalog(SettingDefinition.Integer(MaxConcurrentName, 12, AllLayers, atMostTen))],
            "no-layers" => [new SampleCatalog(SettingDefinition.Integer(MaxConcurrentName, 4, new HashSet<SettingLayer>()))],
            _ => [new SampleCatalog(SettingDefinition.Text(new SettingName("Sample.Api.Token"), "none", AllLayers))],
        };
        var probe = new ServingProbe();

        var thrown = await Assert.ThrowsAsync<PlatformStartupException>(() => PlatformTestHost.CreateBuilder()
            .WithProvider(PersistenceProvider.Sqlite)
            .WithServices(services =>
            {
                services.AddSingleton<IPlatformModule, RuntimeSettingsModule>();
                foreach (var catalog in catalogs)
                {
                    services.AddSingleton(catalog);
                }

                services.AddHostedService(_ => probe);
            })
            .StartAsync(CancellationToken.None));

        var error = Assert.IsType<HostStartupError>(thrown.Error);
        Assert.Equal(nameof(HostStartupError.Registration), error.Code);
        var inner = Assert.IsType<SettingCatalogError>(error.Inner);
        Assert.Equal(expected, inner.Code);
        Assert.False(inner.IsRetryable);
        Assert.False(probe.Started);
    }

    [Fact]
    public async Task S43_4_A_valid_catalogue_lets_the_host_serve()
    {
        var probe = new ServingProbe();
        await using var host = await StartHostAsync(
            new SampleCatalog(SettingDefinition.Integer(MaxConcurrentName, 10, AllLayers, value => value <= 10)),
            extra: services => services.AddHostedService(_ => probe));

        Assert.True(probe.Started);
    }

    [Fact]
    public async Task S43_4_A_defect_in_a_later_declaration_is_found_after_earlier_valid_ones()
    {
        var thrown = await Assert.ThrowsAsync<PlatformStartupException>(() => PlatformTestHost.CreateBuilder()
            .WithProvider(PersistenceProvider.Sqlite)
            .WithServices(services =>
            {
                services.AddSingleton<IPlatformModule, RuntimeSettingsModule>();
                services.AddSingleton<ISettingCatalog>(new SampleCatalog(
                    MaxConcurrent,
                    SettingDefinition.Boolean(new SettingName("Sample.Runs.Paused"), false, AllLayers),
                    SettingDefinition.Boolean(new SettingName("Sample.Runs.Paused"), true, AllLayers)));
            })
            .StartAsync(CancellationToken.None));

        var error = Assert.IsType<HostStartupError>(thrown.Error);
        var inner = Assert.IsType<SettingCatalogError>(error.Inner);
        Assert.Equal(nameof(SettingCatalogError.DuplicateSettingName), inner.Code);
        Assert.Contains("Sample.Runs.Paused", inner.Detail, StringComparison.Ordinal);
    }

    // S43.5 -----------------------------------------------------------------------------------

    [Fact]
    public async Task S43_5_Reading_an_undeclared_name_returns_NotDeclared_and_issues_no_query()
    {
        // No migration: had the reader queried, it would answer StoreUnavailable instead.
        await using var host = await StartHostAsync(new SampleCatalog(MaxConcurrent), migrate: false);
        var undeclared = SettingDefinition.Integer(new SettingName("Sample.Runs.Undeclared"), 4, AllLayers);

        var read = await ReadAsync(host, undeclared, T1, AccountA);

        Assert.False(read.IsSuccess);
        Assert.Equal(nameof(SettingError.NotDeclared), read.Error.Code);
        Assert.False(read.Error.IsRetryable);
        Assert.Contains("Sample.Runs.Undeclared", read.Error.Detail, StringComparison.Ordinal);

        // The same host does query for a declared name, which is what makes the absence above proof.
        var declared = await ReadAsync(host, MaxConcurrent, T1, AccountA);
        Assert.Equal(nameof(SettingError.StoreUnavailable), declared.Error.Code);
    }

    [Fact]
    public async Task S43_5_Reading_a_declared_integer_as_a_boolean_returns_NotDeclared_and_issues_no_query()
    {
        await using var host = await StartHostAsync(new SampleCatalog(MaxConcurrent), migrate: false);
        var asBoolean = SettingDefinition.Boolean(MaxConcurrentName, false, AllLayers);

        var read = await ReadAsync(host, asBoolean, T1, AccountA);

        Assert.False(read.IsSuccess);
        Assert.Equal(nameof(SettingError.NotDeclared), read.Error.Code);
    }

    [Fact]
    public async Task S43_5_The_catalogue_declaration_applies_not_the_one_the_caller_constructed()
    {
        await using var host = await StartHostWithSampleRowsAsync(MaxConcurrent);

        // The caller's copy admits only Global and defaults to 9; the catalogue's admits all three.
        var callersCopy = SettingDefinition.Integer(MaxConcurrentName, 9, new HashSet<SettingLayer> { SettingLayer.Global });
        var read = await ReadAsync(host, callersCopy, T1, AccountA);

        Assert.Equal(new ResolvedSetting<long>(2, SettingLayer.User), read.Value);
    }

    // S43.6 -----------------------------------------------------------------------------------

    [Theory]
    [InlineData("abc")]
    [InlineData("12")]
    [InlineData(" 7")]
    [InlineData("")]
    public async Task S43_6_A_stored_global_value_that_fails_the_declaration_returns_StoredValueInvalid_not_the_default(string stored)
    {
        var atMostTen = SettingDefinition.Integer(MaxConcurrentName, 4, AllLayers, value => value <= 10);
        await using var host = await StartHostAsync(new SampleCatalog(atMostTen));
        await InsertAsync(host, TenantId.Implicit, "global", MaxConcurrentName.Value, "", "", stored);

        var read = await ReadAsync(host, atMostTen, T1, AccountA);

        Assert.False(read.IsSuccess);
        Assert.Equal(nameof(SettingError.StoredValueInvalid), read.Error.Code);
        Assert.False(read.Error.IsRetryable);
        Assert.Contains("Global", read.Error.Detail, StringComparison.Ordinal);
        Assert.Contains(MaxConcurrentName.Value, read.Error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task S43_6_An_invalid_user_value_is_not_replaced_by_a_valid_tenant_value()
    {
        var atMostTen = SettingDefinition.Integer(MaxConcurrentName, 4, AllLayers, value => value <= 10);
        await using var host = await StartHostAsync(new SampleCatalog(atMostTen));
        await InsertAsync(host, T1, "tenant", MaxConcurrentName.Value, "", "", "6");
        await InsertAsync(host, T1, "user", MaxConcurrentName.Value, AccountA.Id.Issuer, AccountA.Id.Subject, "12");

        var read = await ReadAsync(host, atMostTen, T1, AccountA);

        Assert.Equal(nameof(SettingError.StoredValueInvalid), read.Error.Code);
        Assert.Contains("User", read.Error.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public async Task S43_6_A_boolean_reads_exactly_true_or_false(string stored, bool expected)
    {
        var paused = SettingDefinition.Boolean(new SettingName("Sample.Runs.Paused"), !expected, AllLayers);
        await using var host = await StartHostAsync(new SampleCatalog(paused));
        await InsertAsync(host, TenantId.Implicit, "global", "Sample.Runs.Paused", "", "", stored);

        var read = await ReadAsync(host, paused, T1, AccountA);

        Assert.Equal(new ResolvedSetting<bool>(expected, SettingLayer.Global), read.Value);
    }

    [Theory]
    [InlineData("True")]
    [InlineData("1")]
    [InlineData("yes")]
    public async Task S43_6_A_boolean_stored_in_any_other_form_is_invalid(string stored)
    {
        var paused = SettingDefinition.Boolean(new SettingName("Sample.Runs.Paused"), false, AllLayers);
        await using var host = await StartHostAsync(new SampleCatalog(paused));
        await InsertAsync(host, TenantId.Implicit, "global", "Sample.Runs.Paused", "", "", stored);

        var read = await ReadAsync(host, paused, T1, AccountA);

        Assert.Equal(nameof(SettingError.StoredValueInvalid), read.Error.Code);
    }

    [Fact]
    public async Task S43_6_Text_and_negative_integers_read_as_stored()
    {
        var label = SettingDefinition.Text(new SettingName("Sample.Runs.Label"), "default", AllLayers);
        var offset = SettingDefinition.Integer(new SettingName("Sample.Runs.Offset"), 0, AllLayers);
        await using var host = await StartHostAsync(new SampleCatalog(label, offset));
        await InsertAsync(host, TenantId.Implicit, "global", "Sample.Runs.Label", "", "", " spaced label ");
        await InsertAsync(host, T1, "tenant", "Sample.Runs.Offset", "", "", "-3");

        Assert.Equal(new ResolvedSetting<string>(" spaced label ", SettingLayer.Global), (await ReadAsync(host, label, T1, AccountA)).Value);
        Assert.Equal(new ResolvedSetting<long>(-3, SettingLayer.Tenant), (await ReadAsync(host, offset, T1, AccountA)).Value);
    }

    // S43.7 -----------------------------------------------------------------------------------

    [Fact]
    public async Task S43_7_With_the_store_unreachable_a_read_returns_StoreUnavailable_not_the_default()
    {
        var unreachable = $"Data Source={Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}", "x.db")}";
        await using var host = await PlatformTestHost.CreateBuilder()
            .WithProvider(PersistenceProvider.Sqlite)
            .WithSetting("Persistence:ConnectionString", unreachable)
            .WithServices(services =>
            {
                services.AddSingleton<IPlatformModule, RuntimeSettingsModule>();
                services.AddSingleton<ISettingCatalog>(new SampleCatalog(MaxConcurrent));
            })
            .StartAsync(CancellationToken.None);

        var read = await ReadAsync(host, MaxConcurrent, T1, AccountA);

        Assert.False(read.IsSuccess);
        Assert.Equal(nameof(SettingError.StoreUnavailable), read.Error.Code);
        Assert.True(read.Error.IsRetryable);
    }

    // S43.8 -----------------------------------------------------------------------------------

    [Fact]
    public Task S43_8_A_tenant_row_for_T1_is_not_returned_to_a_read_in_T2() =>
        RuntimeSettingsContract.TenantRowStaysInItsTenantAsync(StartSqliteAsync);

    [Fact]
    public Task S43_8_The_migration_applies_once() =>
        RuntimeSettingsContract.MigrationAppliesOnceAsync(StartSqliteAsync);

    [Fact]
    public Task S43_8_The_table_rejects_rows_that_break_its_layer_rules() =>
        RuntimeSettingsContract.TableRejectsMalformedRowsAsync(StartSqliteAsync);

    private static Task<IPlatformTestHost> StartSqliteAsync(Action<IServiceCollection> configure) =>
        PlatformTestHost.CreateBuilder()
            .WithProvider(PersistenceProvider.Sqlite)
            .WithServices(configure)
            .StartAsync(CancellationToken.None);

    // Helpers ---------------------------------------------------------------------------------

    private static async Task<IPlatformTestHost> StartHostAsync(
        ISettingCatalog catalog, bool migrate = true, Action<IServiceCollection>? extra = null)
    {
        var host = await PlatformTestHost.CreateBuilder()
            .WithProvider(PersistenceProvider.Sqlite)
            .WithServices(services =>
            {
                services.AddSingleton<IPlatformModule, RuntimeSettingsModule>();
                services.AddSingleton(catalog);
                extra?.Invoke(services);
            })
            .StartAsync(CancellationToken.None);

        if (migrate)
        {
            var migrated = await host.Services.GetRequiredService<IMigrationRunner>().ApplyAsync(CancellationToken.None);
            Assert.True(migrated.IsSuccess);
        }

        return host;
    }

    /// <summary>Global 8, tenant T1 6, and user (T1, account A) 2 — the rows S43.2 and S43.3 read.</summary>
    private static async Task<IPlatformTestHost> StartHostWithSampleRowsAsync(SettingDefinition declared)
    {
        var host = await StartHostAsync(new SampleCatalog(declared));
        await InsertAsync(host, TenantId.Implicit, "global", MaxConcurrentName.Value, "", "", "8");
        await InsertAsync(host, T1, "tenant", MaxConcurrentName.Value, "", "", "6");
        await InsertAsync(host, T1, "user", MaxConcurrentName.Value, AccountA.Id.Issuer, AccountA.Id.Subject, "2");
        return host;
    }

    internal static async Task<Result<ResolvedSetting<T>, SettingError>> ReadAsync<T>(
        IPlatformTestHost host, SettingDefinition<T> setting, TenantId tenant, Principal principal)
        where T : notnull
    {
        var reader = host.Services.GetRequiredService<ISettingReader>();
        using (host.Services.GetRequiredService<IOperationScopeFactory>().Begin(tenant, principal))
        {
            return await reader.GetAsync(setting, CancellationToken.None);
        }
    }

    /// <summary>Writes a row by raw SQL, beneath the module — the writer is S44's.</summary>
    internal static async Task<bool> InsertAsync(
        IPlatformTestHost host, TenantId tenant, string layer, string name, string issuer, string subject, string value)
    {
        var unitOfWork = host.Services.GetRequiredService<IUnitOfWork>();
        var ambient = host.Services.GetRequiredService<IAmbientTransactionAccessor>();
        var result = await unitOfWork.ExecuteAsync(
            TransactionIntent.Write,
            async ct =>
            {
                await using var command = ambient.Current!.Connection.CreateCommand();
                command.Transaction = ambient.Current!.Transaction;
                command.CommandText = """
                    INSERT INTO runtime_setting (tenant, layer, name, principal_issuer, principal_subject, value)
                    VALUES (@tenant, @layer, @name, @issuer, @subject, @value);
                    """;
                foreach (var (parameterName, parameterValue) in new[]
                {
                    ("@tenant", tenant.ToString()), ("@layer", layer), ("@name", name),
                    ("@issuer", issuer), ("@subject", subject), ("@value", value),
                })
                {
                    var parameter = command.CreateParameter();
                    parameter.ParameterName = parameterName;
                    parameter.Value = parameterValue;
                    command.Parameters.Add(parameter);
                }

                return await command.ExecuteNonQueryAsync(ct);
            },
            CancellationToken.None);

        return result.IsSuccess;
    }

    internal static async Task<long> ScalarAsync(IPlatformTestHost host, string sql)
    {
        var unitOfWork = host.Services.GetRequiredService<IUnitOfWork>();
        var ambient = host.Services.GetRequiredService<IAmbientTransactionAccessor>();
        var result = await unitOfWork.ExecuteAsync(
            TransactionIntent.ReadOnly,
            async ct =>
            {
                await using var command = ambient.Current!.Connection.CreateCommand();
                command.Transaction = ambient.Current!.Transaction;
                command.CommandText = sql;
                return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
            },
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        return result.Value;
    }

    internal sealed class SampleCatalog(params SettingDefinition[] declares) : ISettingCatalog
    {
        public IReadOnlyCollection<SettingDefinition> Declares { get; } = declares;
    }

    /// <summary>A hosted service whose start marks the host as having begun to serve.</summary>
    private sealed class ServingProbe : IHostedService
    {
        public bool Started { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            Started = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

/// <summary>S43.8, written once and run against each provider.</summary>
internal static class RuntimeSettingsContract
{
    internal delegate Task<IPlatformTestHost> HostStarter(Action<IServiceCollection> configure);

    private static void Compose(IServiceCollection services)
    {
        services.AddSingleton<IPlatformModule, RuntimeSettingsModule>();
        services.AddSingleton<ISettingCatalog>(new RuntimeSettingsTests.SampleCatalog(RuntimeSettingsTests.MaxConcurrent));
    }

    internal static async Task TenantRowStaysInItsTenantAsync(HostStarter start)
    {
        await using var host = await start(Compose);
        Assert.True((await host.Services.GetRequiredService<IMigrationRunner>().ApplyAsync(CancellationToken.None)).IsSuccess);
        var name = RuntimeSettingsTests.MaxConcurrentName.Value;
        var account = RuntimeSettingsTests.AccountA;
        Assert.True(await RuntimeSettingsTests.InsertAsync(host, RuntimeSettingsTests.T1, "tenant", name, "", "", "6"));
        Assert.True(await RuntimeSettingsTests.InsertAsync(
            host, RuntimeSettingsTests.T1, "user", name, account.Id.Issuer, account.Id.Subject, "2"));

        var inT1 = await RuntimeSettingsTests.ReadAsync(host, RuntimeSettingsTests.MaxConcurrent, RuntimeSettingsTests.T1, account);
        var inT2 = await RuntimeSettingsTests.ReadAsync(host, RuntimeSettingsTests.MaxConcurrent, RuntimeSettingsTests.T2, account);

        Assert.Equal(new ResolvedSetting<long>(2, SettingLayer.User), inT1.Value);
        Assert.Equal(new ResolvedSetting<long>(4, null), inT2.Value);
    }

    internal static async Task MigrationAppliesOnceAsync(HostStarter start)
    {
        await using var host = await start(Compose);
        var runner = host.Services.GetRequiredService<IMigrationRunner>();

        Assert.True((await runner.ApplyAsync(CancellationToken.None)).IsSuccess);
        Assert.True((await runner.ApplyAsync(CancellationToken.None)).IsSuccess);

        Assert.Equal(1, await RuntimeSettingsTests.ScalarAsync(
            host,
            "SELECT COUNT(*) FROM platform_migrations_runtime_settings WHERE name = '0001_create_runtime_settings';"));
        var status = await runner.GetStatusAsync(CancellationToken.None);
        var module = Assert.Single(status.Value, entry => entry.Module.Value == "RuntimeSettings");
        Assert.Empty(module.Pending);
        Assert.Empty(module.Surplus);
    }

    internal static async Task TableRejectsMalformedRowsAsync(HostStarter start)
    {
        await using var host = await start(Compose);
        Assert.True((await host.Services.GetRequiredService<IMigrationRunner>().ApplyAsync(CancellationToken.None)).IsSuccess);
        var name = RuntimeSettingsTests.MaxConcurrentName.Value;
        var t1 = RuntimeSettingsTests.T1;

        Assert.False(await RuntimeSettingsTests.InsertAsync(host, t1, "global", name, "", "", "8"));
        Assert.False(await RuntimeSettingsTests.InsertAsync(host, t1, "tenant", name, "issuer-a", "", "6"));
        Assert.False(await RuntimeSettingsTests.InsertAsync(host, t1, "tenant", name, "", "account-a", "6"));
        Assert.False(await RuntimeSettingsTests.InsertAsync(host, t1, "user", name, "", "", "2"));
        Assert.False(await RuntimeSettingsTests.InsertAsync(host, t1, "machine", name, "", "", "2"));

        // The well-formed rows each insert, so the rejections above are the CHECKs' and not a typo.
        Assert.True(await RuntimeSettingsTests.InsertAsync(host, TenantId.Implicit, "global", name, "", "", "8"));
        Assert.True(await RuntimeSettingsTests.InsertAsync(host, t1, "tenant", name, "", "", "6"));
        Assert.True(await RuntimeSettingsTests.InsertAsync(host, t1, "user", name, "issuer-a", "account-a", "2"));
        Assert.Equal(3, await RuntimeSettingsTests.ScalarAsync(host, "SELECT COUNT(*) FROM runtime_setting;"));
    }
}
