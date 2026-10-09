using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
/// on SQLite. S44: an administrator changes a setting at a layer through the writer, authorized and
/// audited. <c>RuntimeSettingsPostgresTests</c> repeats S43.8 on PostgreSQL.</summary>
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

    // S44.1 -----------------------------------------------------------------------------------

    internal static readonly SettingDefinition<string> Banner =
        SettingDefinition.Text(new SettingName("Sample.Banner"), "", AllLayers);

    [Fact]
    public async Task S44_1_In_Local_the_system_principal_sets_the_global_value_and_one_Required_record_names_it()
    {
        var sink = DurableSink();
        await using var host = await StartWriterHostAsync(sink, local: true);

        var set = await SetAsync(host, MaxConcurrent, SettingLayer.Global, 8L, TenantId.Implicit, FakePrincipals.System);

        Assert.True(set.IsSuccess);
        Assert.Equal(new ResolvedSetting<long>(8, SettingLayer.Global), (await ReadAsync(host, MaxConcurrent, T1, AccountA)).Value);
        var record = Assert.Single(sink.Received);
        Assert.Equal(RuntimeSettingsAuditActions.SettingSet, record.Action);
        Assert.Equal(AuditClass.Required, record.Class);
        Assert.Equal(AuditOutcome.Allowed, record.Outcome);
        Assert.Equal(new ResourceRef("RuntimeSetting", "global/Sample.Runs.MaxConcurrent"), record.Resource);
        Assert.Equal(FakePrincipals.System.Id, record.Actor);
    }

    // S44.2 -----------------------------------------------------------------------------------

    [Fact]
    public async Task S44_2_Account_A_with_no_grant_is_denied_a_tenant_write_and_only_the_denial_is_audited()
    {
        var sink = DurableSink();
        await using var host = await StartWriterHostAsync(sink);

        var set = await SetAsync(host, MaxConcurrent, SettingLayer.Tenant, 6L, T1, AccountA);

        Assert.False(set.IsSuccess);
        Assert.Equal(nameof(SettingError.PermissionDenied), set.Error.Code);
        Assert.False(set.Error.IsRetryable);
        Assert.Equal(0, await RowCountAsync(host));
        var record = Assert.Single(sink.Received);
        Assert.Equal(PlatformAuditActions.AuthorizationDenied, record.Action);
        Assert.Equal(new ResourceRef("RuntimeSetting", MaxConcurrentName.Value), record.Resource);
    }

    [Fact]
    public async Task S44_2_A_grant_of_WriteTenant_on_the_setting_admits_the_write_and_no_write_to_another_setting()
    {
        var other = SettingDefinition.Integer(new SettingName("Sample.Runs.Other"), 1, AllLayers);
        var sink = DurableSink();
        await using var host = await StartWriterHostAsync(
            sink, extra: GrantWriteTenantOnMaxConcurrent, declares: [MaxConcurrent, other]);

        var granted = await SetAsync(host, MaxConcurrent, SettingLayer.Tenant, 6L, T1, AccountA);
        var elsewhere = await SetAsync(host, other, SettingLayer.Tenant, 6L, T1, AccountA);

        Assert.True(granted.IsSuccess);
        Assert.Equal(new ResolvedSetting<long>(6, SettingLayer.Tenant), (await ReadAsync(host, MaxConcurrent, T1, AccountA)).Value);
        Assert.Equal(new ResourceRef("RuntimeSetting", "tenant/Sample.Runs.MaxConcurrent"), sink.Received[0].Resource);
        Assert.Equal(nameof(SettingError.PermissionDenied), elsewhere.Error.Code);
        Assert.Equal(1, await RowCountAsync(host));
    }

    [Fact]
    public async Task S44_2_A_grant_of_WriteTenant_does_not_admit_a_global_write()
    {
        var sink = DurableSink();
        await using var host = await StartWriterHostAsync(sink, extra: GrantWriteTenantOnMaxConcurrent);

        var set = await SetAsync(host, MaxConcurrent, SettingLayer.Global, 8L, T1, AccountA);

        Assert.Equal(nameof(SettingError.PermissionDenied), set.Error.Code);
        Assert.Equal(0, await RowCountAsync(host));
    }

    [Fact]
    public async Task S44_2_A_global_write_from_a_tenant_is_the_installation_s_value_in_every_tenant()
    {
        var sink = DurableSink();
        await using var host = await StartWriterHostAsync(
            sink,
            extra: services => services.TryAddEnumerable(ServiceDescriptor.Singleton<IPermissionProvider>(
                new SettingGrantProvider(RuntimeSettingsPermissions.WriteGlobal, MaxConcurrentName))));

        var set = await SetAsync(host, MaxConcurrent, SettingLayer.Global, 8L, T1, AccountA);

        Assert.True(set.IsSuccess);
        Assert.Equal(new ResolvedSetting<long>(8, SettingLayer.Global), (await ReadAsync(host, MaxConcurrent, T2, AccountA)).Value);
        Assert.Equal(new ResourceRef("RuntimeSetting", "global/Sample.Runs.MaxConcurrent"), Assert.Single(sink.Received).Resource);
    }

    // S44.3 -----------------------------------------------------------------------------------

    [Fact]
    public async Task S44_3_A_boolean_and_a_text_value_read_back_as_written()
    {
        var enabled = SettingDefinition.Boolean(new SettingName("Sample.Runs.Enabled"), false, AllLayers);
        await using var host = await StartWriterHostAsync(DurableSink(), declares: [enabled, Banner]);

        Assert.True((await SetAsync(host, enabled, SettingLayer.User, true, T1, AccountA)).IsSuccess);
        Assert.True((await SetAsync(host, Banner, SettingLayer.User, " Hello, 1.5 ", T1, AccountA)).IsSuccess);

        Assert.Equal(new ResolvedSetting<bool>(true, SettingLayer.User), (await ReadAsync(host, enabled, T1, AccountA)).Value);
        Assert.Equal(new ResolvedSetting<string>(" Hello, 1.5 ", SettingLayer.User), (await ReadAsync(host, Banner, T1, AccountA)).Value);
    }

    [Fact]
    public async Task S44_3_Account_A_sets_its_own_value_with_no_grant_and_only_A_in_T1_reads_it()
    {
        var accountB = FakePrincipals.Account("issuer-a", "account-b");
        var sink = DurableSink();
        await using var host = await StartWriterHostAsync(sink);
        await InsertAsync(host, TenantId.Implicit, "global", MaxConcurrentName.Value, "", "", "8");
        await InsertAsync(host, T1, "tenant", MaxConcurrentName.Value, "", "", "6");

        var set = await SetAsync(host, MaxConcurrent, SettingLayer.User, 2L, T1, AccountA);

        Assert.True(set.IsSuccess);
        Assert.Equal(new ResolvedSetting<long>(2, SettingLayer.User), (await ReadAsync(host, MaxConcurrent, T1, AccountA)).Value);
        Assert.Equal(new ResolvedSetting<long>(6, SettingLayer.Tenant), (await ReadAsync(host, MaxConcurrent, T1, accountB)).Value);
        Assert.Equal(new ResolvedSetting<long>(8, SettingLayer.Global), (await ReadAsync(host, MaxConcurrent, T2, AccountA)).Value);
        var record = Assert.Single(sink.Received);
        Assert.Equal(new ResourceRef("RuntimeSetting", "user/Sample.Runs.MaxConcurrent"), record.Resource);
        Assert.Equal(T1, record.Tenant);
    }

    [Fact]
    public async Task S44_3_Writing_the_user_layer_again_replaces_the_value()
    {
        var sink = DurableSink();
        await using var host = await StartWriterHostAsync(sink);

        await SetAsync(host, MaxConcurrent, SettingLayer.User, 2L, T1, AccountA);
        var again = await SetAsync(host, MaxConcurrent, SettingLayer.User, 3L, T1, AccountA);

        Assert.True(again.IsSuccess);
        Assert.Equal(new ResolvedSetting<long>(3, SettingLayer.User), (await ReadAsync(host, MaxConcurrent, T1, AccountA)).Value);
        Assert.Equal(1, await RowCountAsync(host));
        Assert.Equal(2, sink.Received.Count);
    }

    // S44.4 -----------------------------------------------------------------------------------

    public static TheoryData<string, string, SettingLayer> UnavailableLayers => new()
    {
        { "delegated", "T1", SettingLayer.User },
        { "system", "T1", SettingLayer.User },
        { "account", "implicit", SettingLayer.Tenant },
        { "account", "implicit", SettingLayer.User },
        { "system", "implicit", SettingLayer.Tenant },
    };

    [Theory]
    [MemberData(nameof(UnavailableLayers))]
    public async Task S44_4_A_layer_that_does_not_exist_for_the_writer_is_unavailable_with_no_row_and_no_record(
        string kind, string tenantName, SettingLayer layer)
    {
        var principal = kind switch
        {
            "delegated" => FakePrincipals.Delegated("issuer-a", "account-a"),
            "system" => FakePrincipals.System,
            _ => AccountA,
        };
        var tenant = tenantName == "T1" ? T1 : TenantId.Implicit;
        var sink = DurableSink();
        await using var host = await StartWriterHostAsync(sink, local: true, extra: GrantWriteTenantOnMaxConcurrent);

        var set = await SetAsync(host, MaxConcurrent, layer, 2L, tenant, principal);
        var clear = await ClearAsync(host, MaxConcurrent, layer, tenant, principal);

        Assert.Equal(nameof(SettingError.LayerUnavailable), set.Error.Code);
        Assert.Equal(nameof(SettingError.LayerUnavailable), clear.Error.Code);
        Assert.Equal(0, await RowCountAsync(host));
        Assert.Empty(sink.Received);
    }

    // S44.5 -----------------------------------------------------------------------------------

    [Fact]
    public async Task S44_5_A_layer_the_declaration_does_not_admit_is_refused_before_anything_else()
    {
        var globalOnly = SettingDefinition.Integer(
            MaxConcurrentName, 4, new HashSet<SettingLayer> { SettingLayer.Global });
        var sink = DurableSink();
        await using var host = await StartWriterHostAsync(sink, declares: [globalOnly]);

        var set = await SetAsync(host, globalOnly, SettingLayer.User, 2L, T1, AccountA);
        var clear = await ClearAsync(host, globalOnly, SettingLayer.Tenant, T1, AccountA);

        Assert.Equal(nameof(SettingError.LayerNotAdmitted), set.Error.Code);
        Assert.Equal(nameof(SettingError.LayerNotAdmitted), clear.Error.Code);
        Assert.Equal(0, await RowCountAsync(host));
        Assert.Empty(sink.Received);
    }

    [Fact]
    public async Task S44_5_A_value_outside_the_rule_is_invalid_and_the_error_does_not_echo_it()
    {
        var atMostTen = SettingDefinition.Integer(MaxConcurrentName, 4, AllLayers, value => value <= 10);
        var sink = DurableSink();
        await using var host = await StartWriterHostAsync(sink, declares: [atMostTen]);

        var set = await SetAsync(host, atMostTen, SettingLayer.User, 12L, T1, AccountA);
        var atLimit = await SetAsync(host, atMostTen, SettingLayer.User, 10L, T1, AccountA);

        Assert.Equal(nameof(SettingError.InvalidValue), set.Error.Code);
        Assert.False(set.Error.IsRetryable);
        Assert.DoesNotContain("12", set.Error.Detail, StringComparison.Ordinal);
        Assert.True(atLimit.IsSuccess);
        Assert.Equal(1, await RowCountAsync(host));
    }

    [Fact]
    public async Task S44_5_An_invalid_value_is_refused_before_permission_is_evaluated()
    {
        var atMostTen = SettingDefinition.Integer(MaxConcurrentName, 4, AllLayers, value => value <= 10);
        var sink = DurableSink();
        await using var host = await StartWriterHostAsync(sink, declares: [atMostTen]);

        var set = await SetAsync(host, atMostTen, SettingLayer.Tenant, 12L, T1, AccountA);

        Assert.Equal(nameof(SettingError.InvalidValue), set.Error.Code);
        Assert.Empty(sink.Received);
    }

    [Fact]
    public async Task S44_5_A_setting_the_catalogue_does_not_declare_is_not_written()
    {
        var undeclared = SettingDefinition.Integer(new SettingName("Sample.Undeclared"), 1, AllLayers);
        var sameNameOtherType = SettingDefinition.Boolean(MaxConcurrentName, false, AllLayers);
        var sink = DurableSink();
        await using var host = await StartWriterHostAsync(sink);

        Result<SettingError>[] results =
        [
            await SetAsync(host, undeclared, SettingLayer.User, 1L, T1, AccountA),
            await SetAsync(host, sameNameOtherType, SettingLayer.User, true, T1, AccountA),
            await ClearAsync(host, undeclared, SettingLayer.User, T1, AccountA),
            await ClearAsync(host, sameNameOtherType, SettingLayer.User, T1, AccountA),
        ];

        Assert.All(results, result => Assert.Equal(nameof(SettingError.NotDeclared), result.Error.Code));
        Assert.Equal(0, await RowCountAsync(host));
    }

    // S44.6 -----------------------------------------------------------------------------------

    [Fact]
    public async Task S44_6_Clearing_the_tenant_row_falls_the_next_read_through_to_global_with_one_cleared_record()
    {
        var sink = DurableSink();
        await using var host = await StartWriterHostAsync(sink, extra: GrantWriteTenantOnMaxConcurrent);
        await InsertAsync(host, TenantId.Implicit, "global", MaxConcurrentName.Value, "", "", "8");
        await InsertAsync(host, T1, "tenant", MaxConcurrentName.Value, "", "", "6");
        await InsertAsync(host, T2, "tenant", MaxConcurrentName.Value, "", "", "5");

        var clear = await ClearAsync(host, MaxConcurrent, SettingLayer.Tenant, T1, AccountA);

        Assert.True(clear.IsSuccess);
        Assert.Equal(new ResolvedSetting<long>(8, SettingLayer.Global), (await ReadAsync(host, MaxConcurrent, T1, AccountA)).Value);
        Assert.Equal(new ResolvedSetting<long>(5, SettingLayer.Tenant), (await ReadAsync(host, MaxConcurrent, T2, AccountA)).Value);
        var record = Assert.Single(sink.Received);
        Assert.Equal(RuntimeSettingsAuditActions.SettingCleared, record.Action);
        Assert.Equal(AuditClass.Required, record.Class);
        Assert.Equal(new ResourceRef("RuntimeSetting", "tenant/Sample.Runs.MaxConcurrent"), record.Resource);
    }

    [Fact]
    public async Task S44_6_Clearing_a_row_that_is_not_there_succeeds_and_writes_no_record()
    {
        var sink = DurableSink();
        await using var host = await StartWriterHostAsync(sink);

        var clear = await ClearAsync(host, MaxConcurrent, SettingLayer.User, T1, AccountA);

        Assert.True(clear.IsSuccess);
        Assert.Empty(sink.Received);
    }

    [Fact]
    public async Task S44_6_Clearing_needs_the_same_permission_as_setting()
    {
        var sink = DurableSink();
        await using var host = await StartWriterHostAsync(sink);
        await InsertAsync(host, T1, "tenant", MaxConcurrentName.Value, "", "", "6");

        var clear = await ClearAsync(host, MaxConcurrent, SettingLayer.Tenant, T1, AccountA);

        Assert.Equal(nameof(SettingError.PermissionDenied), clear.Error.Code);
        Assert.Equal(1, await RowCountAsync(host));
        Assert.Equal(PlatformAuditActions.AuthorizationDenied, Assert.Single(sink.Received).Action);
    }

    // S44.7 -----------------------------------------------------------------------------------

    [Fact]
    public async Task S44_7_A_failing_durable_sink_leaves_the_previous_value_and_answers_a_retryable_failure()
    {
        var sink = DurableSink();
        await using var host = await StartWriterHostAsync(sink);
        Assert.True((await SetAsync(host, MaxConcurrent, SettingLayer.User, 2L, T1, AccountA)).IsSuccess);
        sink.FailNextWith(_ => Result<AuditError>.Failure(AuditError.SinkUnavailable("settings")));

        var set = await SetAsync(host, MaxConcurrent, SettingLayer.User, 3L, T1, AccountA);
        var clear = await ClearAsync(host, MaxConcurrent, SettingLayer.User, T1, AccountA);

        Assert.Equal(nameof(SettingError.StoreUnavailable), set.Error.Code);
        Assert.True(set.Error.IsRetryable);
        Assert.Equal(nameof(SettingError.StoreUnavailable), clear.Error.Code);
        Assert.Equal(new ResolvedSetting<long>(2, SettingLayer.User), (await ReadAsync(host, MaxConcurrent, T1, AccountA)).Value);
    }

    // S44.8 -----------------------------------------------------------------------------------

    [Fact]
    public async Task S44_8_A_permission_provider_that_cannot_answer_makes_the_write_retryable_with_no_row()
    {
        var faulting = new FakePermissionProvider
        {
            Response = Result<IReadOnlySet<PermissionName>, AuthorizationError>.Failure(
                AuthorizationError.ProviderUnavailable(new PermissionProviderName("Platform.Testing.PermissionProvider"))),
        };
        var sink = DurableSink();
        await using var host = await StartWriterHostAsync(
            sink, extra: services => services.TryAddEnumerable(ServiceDescriptor.Singleton<IPermissionProvider>(faulting)));

        var set = await SetAsync(host, MaxConcurrent, SettingLayer.Tenant, 6L, T1, AccountA);
        var clear = await ClearAsync(host, MaxConcurrent, SettingLayer.Global, T1, AccountA);

        Assert.Equal(nameof(SettingError.AuthorizationUnavailable), set.Error.Code);
        Assert.True(set.Error.IsRetryable);
        Assert.Equal(nameof(SettingError.AuthorizationUnavailable), clear.Error.Code);
        Assert.Equal(0, await RowCountAsync(host));
    }

    [Fact]
    public async Task S44_8_A_denial_whose_audit_record_cannot_be_written_is_retryable()
    {
        var sink = DurableSink();
        sink.FailNextWith(_ => Result<AuditError>.Failure(AuditError.SinkUnavailable("settings")));
        await using var host = await StartWriterHostAsync(sink);

        var set = await SetAsync(host, MaxConcurrent, SettingLayer.Tenant, 6L, T1, AccountA);

        Assert.Equal(nameof(SettingError.AuthorizationUnavailable), set.Error.Code);
        Assert.Equal(0, await RowCountAsync(host));
    }

    [Fact]
    public void S44_The_module_declares_its_two_write_permissions()
    {
        var services = new ServiceCollection();
        new RuntimeSettingsModule().Register(services);
        using var provider = services.BuildServiceProvider();

        var catalog = Assert.Single(provider.GetServices<IPermissionCatalog>());

        Assert.Equal(
            ["Platform.RuntimeSettings.WriteGlobal", "Platform.RuntimeSettings.WriteTenant"],
            catalog.Declares.Select(permission => permission.Value).Order());
    }

    private static void GrantWriteTenantOnMaxConcurrent(IServiceCollection services) =>
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPermissionProvider>(
            new SettingGrantProvider(RuntimeSettingsPermissions.WriteTenant, MaxConcurrentName)));

    // Helpers ---------------------------------------------------------------------------------

    private static async Task<IPlatformTestHost> StartHostAsync(
        ISettingCatalog catalog,
        bool migrate = true,
        Action<IServiceCollection>? extra = null,
        bool local = false)
    {
        var builder = PlatformTestHost.CreateBuilder().WithProvider(PersistenceProvider.Sqlite);
        if (local)
        {
            builder = builder.WithSetting("CompositionProfile", nameof(CompositionProfile.Local));
        }

        var host = await builder
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

    /// <summary>Writes a row by raw SQL, beneath the module and its writer's checks.</summary>
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

    private static RecordingAuditSink DurableSink() => new("settings", isDurable: true);

    /// <summary>A SQLite host composing the module, the sample catalogue and <paramref name="sink"/>,
    /// Operated unless <paramref name="local"/>.</summary>
    private static Task<IPlatformTestHost> StartWriterHostAsync(
        RecordingAuditSink sink,
        bool local = false,
        Action<IServiceCollection>? extra = null,
        SettingDefinition[]? declares = null) =>
        StartHostAsync(
            new SampleCatalog(declares ?? [MaxConcurrent, Banner]),
            local: local,
            extra: services =>
            {
                services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditSink>(sink));
                extra?.Invoke(services);
            });

    internal static async Task<Result<SettingError>> SetAsync<T>(
        IPlatformTestHost host, SettingDefinition<T> setting, SettingLayer layer, T value, TenantId tenant, Principal principal)
        where T : notnull
    {
        var writer = host.Services.GetRequiredService<ISettingWriter>();
        using (host.Services.GetRequiredService<IOperationScopeFactory>().Begin(tenant, principal))
        {
            return await writer.SetAsync(setting, layer, value, CancellationToken.None);
        }
    }

    private static async Task<Result<SettingError>> ClearAsync(
        IPlatformTestHost host, SettingDefinition setting, SettingLayer layer, TenantId tenant, Principal principal)
    {
        var writer = host.Services.GetRequiredService<ISettingWriter>();
        using (host.Services.GetRequiredService<IOperationScopeFactory>().Begin(tenant, principal))
        {
            return await writer.ClearAsync(setting, layer, CancellationToken.None);
        }
    }

    private static Task<long> RowCountAsync(IPlatformTestHost host) =>
        ScalarAsync(host, "SELECT COUNT(*) FROM runtime_setting;");

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

    /// <summary>Grants one permission, and only on the one setting it names.</summary>
    internal sealed class SettingGrantProvider(PermissionName permission, SettingName setting) : IPermissionProvider
    {
        public PermissionProviderName Name { get; } = new("Tests.RuntimeSettings");

        public Task<Result<IReadOnlySet<PermissionName>, AuthorizationError>> GrantsAsync(
            Principal principal, TenantId tenant, ResourceRef? resource, CancellationToken cancellationToken)
        {
            IReadOnlySet<PermissionName> grants = resource == new ResourceRef("RuntimeSetting", setting.Value)
                ? new HashSet<PermissionName> { permission }
                : new HashSet<PermissionName>();
            return Task.FromResult(Result<IReadOnlySet<PermissionName>, AuthorizationError>.Success(grants));
        }
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
