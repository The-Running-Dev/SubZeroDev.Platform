using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Testing;

namespace SubZeroDev.Platform.Tests;

/// <summary>S4: authorization names, providers and the evaluator.</summary>
public sealed class AuthorizationTests
{
    private static readonly TenantId TestTenant = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));
    private static readonly PermissionName TestPermission = new("Test.Widget.Read");

    [Fact]
    public void S4_1_EvaluateAsync_declares_no_principal_and_no_tenant_parameter()
    {
        var parameters = typeof(IAuthorizationEvaluator)
            .GetMethod(nameof(IAuthorizationEvaluator.EvaluateAsync))!
            .GetParameters();

        Assert.DoesNotContain(parameters, p => p.ParameterType == typeof(Principal));
        Assert.DoesNotContain(parameters, p => p.ParameterType == typeof(TenantId));
    }

    [Fact]
    public async Task S4_1_Both_the_principal_and_the_tenant_come_from_the_ambient_scope()
    {
        await using var host = await BuildHostAsync(services => services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IPermissionProvider>(GrantingProvider("granter"))));

        var evaluator = host.Services.GetRequiredService<IAuthorizationEvaluator>();
        var scopeFactory = host.Services.GetRequiredService<IOperationScopeFactory>();

        using (scopeFactory.Begin(TestTenant, FakePrincipals.System))
        {
            var decision = await evaluator.EvaluateAsync(TestPermission, null, CancellationToken.None);
            Assert.Equal(TestTenant, decision.Tenant);
        }
    }

    [Fact]
    public async Task S4_2_Two_providers_granting_produce_one_Allowed_decision_naming_both_sources()
    {
        await using var host = await BuildHostAsync(services =>
        {
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IPermissionProvider>(GrantingProvider("first")));
            services.AddSingleton<IPermissionProvider>(GrantingProvider("second"));
        });

        var decision = await EvaluateAsync(host, FakePrincipals.System);

        Assert.Equal(AuthorizationOutcome.Allowed, decision.Outcome);
        Assert.Equal(
            new[] { "first", "second" },
            decision.Sources.Select(source => source.Value).Order());
    }

    [Fact]
    public async Task S4_2_One_provider_granting_produces_one_source()
    {
        await using var host = await BuildHostAsync(services => services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IPermissionProvider>(GrantingProvider("only"))));

        var decision = await EvaluateAsync(host, FakePrincipals.System);

        Assert.Equal(AuthorizationOutcome.Allowed, decision.Outcome);
        Assert.Equal("only", Assert.Single(decision.Sources).Value);
    }

    [Fact]
    public async Task S4_3_A_denial_carries_an_empty_source_set()
    {
        await using var host = await BuildHostAsync(services => { });

        var decision = await EvaluateAsync(host, FakePrincipals.System);

        Assert.Equal(AuthorizationOutcome.Denied, decision.Outcome);
        Assert.Empty(decision.Sources);
    }

    [Fact]
    public async Task S4_3_An_allowed_decision_never_carries_an_empty_source_set()
    {
        await using var host = await BuildHostAsync(services => services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IPermissionProvider>(GrantingProvider("granter"))));

        var decision = await EvaluateAsync(host, FakePrincipals.System);

        Assert.Equal(AuthorizationOutcome.Allowed, decision.Outcome);
        Assert.NotEmpty(decision.Sources);
    }

    [Fact]
    public async Task S4_4_A_provider_that_cannot_answer_denies_this_request_without_failing_the_evaluation()
    {
        await using var host = await BuildHostAsync(services => services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IPermissionProvider>(new StubPermissionProvider(
                "unavailable",
                (_, _, _) => Result<IReadOnlySet<PermissionName>, AuthorizationError>.Failure(
                    AuthorizationError.ProviderUnavailable(new PermissionProviderName("unavailable")))))));

        // The evaluator's return type alone guarantees it always returns a decision rather than a
        // failure result — awaiting this without a try/catch is the proof.
        var decision = await EvaluateAsync(host, FakePrincipals.System);

        Assert.Equal(AuthorizationOutcome.Denied, decision.Outcome);
        Assert.Empty(decision.Sources);
    }

    [Fact]
    public void S4_4_ProviderUnavailable_is_retryable_so_the_caller_may_retry()
    {
        var error = AuthorizationError.ProviderUnavailable(new PermissionProviderName("store"));

        Assert.True(error.IsRetryable);
    }

    [Fact]
    public void S4_5_A_name_no_catalog_declares_fails_the_undeclared_check()
    {
        var registry = new PermissionCatalogRegistry();
        registry.Register(new StubPermissionCatalog(TestPermission));

        var checkedName = new PermissionName("Test.Widget.Delete");
        var result = registry.EnsureDeclared(checkedName);

        Assert.False(result.IsSuccess);
        Assert.Equal("UnregisteredPermission", result.Error.Code);
        Assert.Contains(checkedName.Value, result.Error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void S4_5_The_undeclared_check_wraps_as_HostStartupError_UnregisteredPermission()
    {
        var registry = new PermissionCatalogRegistry();
        var checkedName = new PermissionName("Test.Widget.Delete");

        var failure = registry.EnsureDeclared(checkedName);
        var wrapped = Hosting.HostStartupError.Registration(failure.Error, failure.Error.Detail);

        Assert.Equal("Registration", wrapped.Code);
        Assert.Equal("UnregisteredPermission", wrapped.Inner?.Code);
    }

    [Fact]
    public void S4_5_A_declared_name_passes_the_check()
    {
        var registry = new PermissionCatalogRegistry();
        registry.Register(new StubPermissionCatalog(TestPermission));

        Assert.True(registry.EnsureDeclared(TestPermission).IsSuccess);
    }

    [Fact]
    public void S4_6_Two_catalogs_declaring_the_same_name_are_rejected_naming_both()
    {
        var registry = new PermissionCatalogRegistry();
        Assert.True(registry.Register(new StubPermissionCatalog(TestPermission)).IsSuccess);

        var second = registry.Register(new StubPermissionCatalog(TestPermission));

        Assert.False(second.IsSuccess);
        Assert.Equal("DuplicatePermissionName", second.Error.Code);
        Assert.Contains(TestPermission.Value, second.Error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task S4_6_Two_modules_declaring_the_same_permission_name_fail_startup()
    {
        var thrown = await Assert.ThrowsAsync<Hosting.PlatformStartupException>(() => PlatformTestHost.CreateBuilder()
            .WithServices(services =>
            {
                services.TryAddEnumerable(
                    ServiceDescriptor.Singleton<IPermissionCatalog>(new StubPermissionCatalog(TestPermission)));
                services.AddSingleton<IPermissionCatalog>(new StubPermissionCatalog(TestPermission));
            })
            .StartAsync(CancellationToken.None));

        var error = Assert.IsType<Hosting.HostStartupError>(thrown.Error);
        Assert.Equal("Registration", error.Code);
        Assert.Equal("DuplicatePermissionName", error.Inner?.Code);
    }

    [Fact]
    public async Task S4_7_In_Local_the_composition_provider_grants_every_declared_permission_to_System()
    {
        await using var host = await PlatformTestHost.CreateBuilder()
            .WithSetting("CompositionProfile", nameof(CompositionProfile.Local))
            .StartAsync(CancellationToken.None);

        var decision = await EvaluateAsync(
            host, FakePrincipals.System, PlatformPermissions.ShareResource);

        Assert.Equal(AuthorizationOutcome.Allowed, decision.Outcome);
        Assert.Contains(decision.Sources, source => source.Value == "Platform.Composition");
    }

    [Fact]
    public async Task S4_7_In_Local_Anonymous_is_granted_nothing()
    {
        await using var host = await PlatformTestHost.CreateBuilder()
            .WithSetting("CompositionProfile", nameof(CompositionProfile.Local))
            .StartAsync(CancellationToken.None);

        var decision = await EvaluateAsync(
            host, FakePrincipals.Anonymous, PlatformPermissions.ShareResource);

        Assert.Equal(AuthorizationOutcome.Denied, decision.Outcome);
    }

    [Fact]
    public async Task S4_7_In_Operated_the_composition_provider_grants_nothing_to_System_or_Anonymous()
    {
        await using var host = await PlatformTestHost.CreateBuilder()
            .StartAsync(CancellationToken.None); // Operated is the test host's default profile.

        var toSystem = await EvaluateAsync(host, FakePrincipals.System, PlatformPermissions.ShareResource);
        var toAnonymous = await EvaluateAsync(host, FakePrincipals.Anonymous, PlatformPermissions.ShareResource);

        Assert.Equal(AuthorizationOutcome.Denied, toSystem.Outcome);
        Assert.Equal(AuthorizationOutcome.Denied, toAnonymous.Outcome);
    }

    [Fact]
    public async Task S4_8_A_denial_writes_exactly_one_Required_audit_record_and_no_provider_writes_its_own()
    {
        var sink = new RecordingAuditSink();
        await using var host = await BuildHostAsync(services =>
        {
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditSink>(sink));

            // A provider that answers without ever touching IAuditWriter — proving the record the
            // evaluator writes is the only one, not a coincidence of an empty provider set.
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IPermissionProvider>(new StubPermissionProvider(
                "silent-denier",
                (_, _, _) => Result<IReadOnlySet<PermissionName>, AuthorizationError>.Success(
                    new HashSet<PermissionName>()))));
        });

        await EvaluateAsync(host, FakePrincipals.System);

        var recorded = Assert.Single(sink.Received);
        Assert.Equal(PlatformAuditActions.AuthorizationDenied, recorded.Action);
        Assert.Equal(AuditClass.Required, recorded.Class);
        Assert.Equal(AuditOutcome.Denied, recorded.Outcome);
    }

    [Fact]
    public void S4_9_A_denial_on_a_visible_resource_is_PermissionDenied_and_not_retryable()
    {
        var error = AuthorizationError.PermissionDenied(TestPermission);

        Assert.Equal("PermissionDenied", error.Code);
        Assert.False(error.IsRetryable);
    }

    [Fact]
    public void S4_9_A_denial_on_a_resource_in_another_tenant_is_ResourceNotVisible_and_not_retryable()
    {
        var error = AuthorizationError.ResourceNotVisible(new ResourceRef("widget", "1"));

        Assert.Equal("ResourceNotVisible", error.Code);
        Assert.False(error.IsRetryable);
    }

    [Fact]
    public void S4_9_PermissionDenied_and_ResourceNotVisible_are_distinct_codes()
    {
        var deniedOnVisible = AuthorizationError.PermissionDenied(TestPermission);
        var deniedOnHidden = AuthorizationError.ResourceNotVisible(new ResourceRef("widget", "1"));

        Assert.NotEqual(deniedOnVisible.Code, deniedOnHidden.Code);
    }

    [Fact]
    public void S24_1_ProviderFailure_is_an_init_only_nullable_AuthorizationError_on_the_decision()
    {
        var property = typeof(AuthorizationDecision).GetProperty(nameof(AuthorizationDecision.ProviderFailure))!;

        Assert.Equal(typeof(AuthorizationError), property.PropertyType);
        Assert.True(new System.Reflection.NullabilityInfoContext().Create(property).ReadState
            == System.Reflection.NullabilityState.Nullable);
        Assert.Contains(
            property.SetMethod!.ReturnParameter.GetRequiredCustomModifiers(),
            modifier => modifier.FullName == "System.Runtime.CompilerServices.IsExternalInit");
    }

    [Fact]
    public async Task S24_2_A_first_provider_error_and_a_second_granting_nothing_deny_carrying_ProviderUnavailable_naming_the_first()
    {
        // The first provider's own error is deliberately not ProviderUnavailable: the decision
        // normalises whatever it returned.
        await using var host = await BuildHostAsync(services =>
        {
            services.AddSingleton<IPermissionProvider>(FailingProvider("first", AuthorizationError.PermissionDenied(TestPermission)));
            services.AddSingleton<IPermissionProvider>(SilentProvider("second"));
        });

        var decision = await EvaluateAsync(host, FakePrincipals.System);

        Assert.Equal(AuthorizationOutcome.Denied, decision.Outcome);
        var failure = Assert.IsType<AuthorizationError>(decision.ProviderFailure);
        Assert.Equal(AuthorizationError.ProviderUnavailable(new PermissionProviderName("first")).Code, failure.Code);
        Assert.Equal(AuthorizationError.ProviderUnavailable(new PermissionProviderName("first")).Detail, failure.Detail);
        Assert.True(failure.IsRetryable);
    }

    [Fact]
    public async Task S24_3_With_the_second_and_third_providers_failing_ProviderFailure_names_the_second()
    {
        await using var host = await BuildHostAsync(services =>
        {
            services.AddSingleton<IPermissionProvider>(SilentProvider("first"));
            services.AddSingleton<IPermissionProvider>(FailingProvider("second", AuthorizationError.ProviderUnavailable(new PermissionProviderName("second"))));
            services.AddSingleton<IPermissionProvider>(FailingProvider("third", AuthorizationError.ProviderUnavailable(new PermissionProviderName("third"))));
        });

        var decision = await EvaluateAsync(host, FakePrincipals.System);

        Assert.Equal(AuthorizationOutcome.Denied, decision.Outcome);
        Assert.Equal(
            AuthorizationError.ProviderUnavailable(new PermissionProviderName("second")).Detail,
            decision.ProviderFailure?.Detail);
    }

    [Fact]
    public async Task S24_4_One_provider_failing_and_another_granting_allow_with_no_ProviderFailure()
    {
        await using var host = await BuildHostAsync(services =>
        {
            services.AddSingleton<IPermissionProvider>(FailingProvider("broken", AuthorizationError.ProviderUnavailable(new PermissionProviderName("broken"))));
            services.AddSingleton<IPermissionProvider>(GrantingProvider("granter"));
        });

        var decision = await EvaluateAsync(host, FakePrincipals.System);

        Assert.Equal(AuthorizationOutcome.Allowed, decision.Outcome);
        Assert.Null(decision.ProviderFailure);
    }

    [Fact]
    public async Task S24_4_A_denial_in_which_every_provider_answered_has_no_ProviderFailure()
    {
        await using var host = await BuildHostAsync(services =>
        {
            services.AddSingleton<IPermissionProvider>(SilentProvider("first"));
            services.AddSingleton<IPermissionProvider>(SilentProvider("second"));
        });

        var decision = await EvaluateAsync(host, FakePrincipals.System);

        Assert.Equal(AuthorizationOutcome.Denied, decision.Outcome);
        Assert.Null(decision.ProviderFailure);
    }

    [Fact]
    public async Task S24_7_A_denial_whose_audit_failed_while_a_provider_failed_carries_both()
    {
        var sink = new RecordingAuditSink();
        sink.FailNextWith(_ => Result<AuditError>.Failure(AuditError.SinkUnavailable("recording")));
        await using var host = await BuildHostAsync(services =>
        {
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditSink>(sink));
            services.AddSingleton<IPermissionProvider>(FailingProvider("broken", AuthorizationError.ProviderUnavailable(new PermissionProviderName("broken"))));
        });

        var decision = await EvaluateAsync(host, FakePrincipals.System);

        Assert.Equal(AuthorizationOutcome.Denied, decision.Outcome);
        Assert.NotNull(decision.AuditFailure);
        Assert.NotNull(decision.ProviderFailure);
    }

    [Fact]
    public async Task S26_1_The_evaluator_called_directly_refuses_a_name_no_catalog_declares()
    {
        // Called directly, not through an endpoint or a tool, so no startup check has seen the name.
        // The provider would grant it: a refusal here is the evaluator's own, not a coincidence of
        // nobody granting the name.
        var undeclared = new PermissionName("Test.Undeclared.Use");
        var consulted = 0;
        var sink = new RecordingAuditSink();
        await using var host = await BuildHostAsync(services =>
        {
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditSink>(sink));
            services.AddSingleton<IPermissionProvider>(new StubPermissionProvider(
                "would-grant",
                (_, _, _) =>
                {
                    consulted++;
                    return Result<IReadOnlySet<PermissionName>, AuthorizationError>.Success(
                        new HashSet<PermissionName> { undeclared });
                }));
        });

        var thrown = await Assert.ThrowsAsync<PlatformContractViolationException>(
            () => EvaluateAsync(host, FakePrincipals.System, undeclared));

        Assert.Equal(PermissionCatalogError.UnregisteredPermission(undeclared).Code, thrown.Error.Code);
        Assert.Equal(
            PermissionCatalogError.UnregisteredPermission(undeclared).Detail,
            Assert.IsType<PermissionCatalogError>(thrown.Error).Detail);
        Assert.Equal(0, consulted);

        // Never a runtime denial (I-A3): no decision was reached, so none was audited.
        Assert.Empty(sink.Received);
    }

    [Fact]
    public async Task S26_2_In_Operated_the_composition_provider_grants_nothing_to_Account_or_Delegated()
    {
        await using var host = await PlatformTestHost.CreateBuilder()
            .StartAsync(CancellationToken.None); // Operated is the test host's default profile.

        var composition = Assert.Single(
            host.Services.GetServices<IPermissionProvider>(),
            provider => provider.Name.Value == "Platform.Composition");

        foreach (var principal in new[] { FakePrincipals.Account(), FakePrincipals.Delegated() })
        {
            foreach (var resource in new ResourceRef?[] { null, new ResourceRef("Organization", "org-1") })
            {
                var granted = await composition.GrantsAsync(principal, TestTenant, resource, CancellationToken.None);

                Assert.True(granted.IsSuccess);
                Assert.Empty(granted.Value);
            }

            // And through the evaluator, for every name Platform itself declares.
            foreach (var permission in new[]
                     {
                         PlatformPermissions.ShareResource,
                         PlatformPermissions.AdministerOrganization,
                         PlatformPermissions.ReadAudit,
                     })
            {
                var decision = await EvaluateAsync(host, principal, permission);

                Assert.Equal(AuthorizationOutcome.Denied, decision.Outcome);
                Assert.Empty(decision.Sources);
            }
        }
    }

    // Every test here evaluates `TestPermission`, so every host declares it: an undeclared name
    // reaching the evaluator is a defect the evaluator refuses (I-A3, S26.1), not a denial.
    private static async Task<IPlatformTestHost> BuildHostAsync(Action<IServiceCollection> configure) =>
        await PlatformTestHost.CreateBuilder()
            .WithServices(services =>
            {
                services.AddSingleton<IPermissionCatalog>(new StubPermissionCatalog(TestPermission));
                configure(services);
            })
            .StartAsync(CancellationToken.None);

    private static async Task<AuthorizationDecision> EvaluateAsync(
        IPlatformTestHost host, Principal principal, PermissionName? permission = null)
    {
        var evaluator = host.Services.GetRequiredService<IAuthorizationEvaluator>();
        var scopeFactory = host.Services.GetRequiredService<IOperationScopeFactory>();

        using (scopeFactory.Begin(TestTenant, principal))
        {
            return await evaluator.EvaluateAsync(permission ?? TestPermission, null, CancellationToken.None);
        }
    }

    private static StubPermissionProvider SilentProvider(string name) =>
        new(
            name,
            (_, _, _) => Result<IReadOnlySet<PermissionName>, AuthorizationError>.Success(
                new HashSet<PermissionName>()));

    private static StubPermissionProvider FailingProvider(string name, AuthorizationError error) =>
        new(name, (_, _, _) => Result<IReadOnlySet<PermissionName>, AuthorizationError>.Failure(error));

    private static StubPermissionProvider GrantingProvider(string name) =>
        new(
            name,
            (_, _, _) => Result<IReadOnlySet<PermissionName>, AuthorizationError>.Success(
                new HashSet<PermissionName> { TestPermission }));
}
