using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Testing;

namespace SubZeroDev.Platform.Tests;

public sealed class TestingHelpersTests
{
    [Fact]
    public void S18_2_Framework_helpers_are_public_and_module_fakes_are_absent()
    {
        var assembly = typeof(FakeClock).Assembly;
        foreach (var name in new[] { "FakeTenantResolver", "FakePermissionProvider", "FakeEntitlementContributor", "AuditInspector" })
        {
            Assert.NotNull(assembly.GetType($"SubZeroDev.Platform.Testing.{name}"));
        }

        Assert.DoesNotContain(assembly.GetExportedTypes(), type =>
            type.Name.StartsWith("Fake", StringComparison.Ordinal) &&
            new[] { "Organization", "Subscription", "Licence" }.Any(part => type.Name.Contains(part, StringComparison.Ordinal)));
        Assert.Equal(PrincipalKind.Anonymous, FakePrincipals.Anonymous.Kind);
        Assert.Equal(PrincipalKind.System, FakePrincipals.System.Kind);
        Assert.Equal(PrincipalKind.Account, FakePrincipals.Account().Kind);
        Assert.Equal(PrincipalKind.Delegated, FakePrincipals.Delegated().Kind);
    }

    [Theory]
    [InlineData(CompositionProfile.Local)]
    [InlineData(CompositionProfile.Operated)]
    public async Task S18_2_Test_host_exposes_its_validated_profile_without_a_setter(CompositionProfile profile)
    {
        var property = typeof(IPlatformTestHost).GetProperty("CompositionProfile");
        Assert.NotNull(property);
        Assert.Null(property.SetMethod);
        await using var host = await PlatformTestHost.CreateBuilder()
            .WithSetting("CompositionProfile", profile.ToString()).StartAsync(CancellationToken.None);
        Assert.Equal(profile, property.GetValue(host));
        Assert.DoesNotContain(host.Services.GetServices<ITenantResolver>(), resolver =>
            resolver.GetType().Name == "FakeTenantResolver");
    }

    [Fact]
    public async Task S18_2_Fakes_defer_or_deny_by_default_and_return_configured_grants_and_errors()
    {
        var tenant = new TenantId(Guid.NewGuid());
        var resolver = new FakeTenantResolver();
        Assert.Null(await resolver.ResolveAsync(CancellationToken.None));
        resolver.Tenant = tenant;
        Assert.Equal(tenant, await resolver.ResolveAsync(CancellationToken.None));

        var permission = new PermissionName("Test.Read");
        var provider = new FakePermissionProvider();
        Assert.Empty((await provider.GrantsAsync(FakePrincipals.Account(), tenant, null, CancellationToken.None)).Value);
        provider.Response = Result<IReadOnlySet<PermissionName>, AuthorizationError>.Success(new HashSet<PermissionName> { permission });
        Assert.Contains(permission, (await provider.GrantsAsync(FakePrincipals.Account(), tenant, null, CancellationToken.None)).Value);
        var permissionError = AuthorizationError.ProviderUnavailable(provider.Name);
        provider.Response = Result<IReadOnlySet<PermissionName>, AuthorizationError>.Failure(permissionError);
        Assert.Equal(permissionError, (await provider.GrantsAsync(FakePrincipals.Account(), tenant, null, CancellationToken.None)).Error);

        var feature = new FeatureName("Test.Paid");
        var contributor = new FakeEntitlementContributor();
        Assert.False((await contributor.GrantsAsync(feature, tenant, CancellationToken.None)).Value);
        contributor.Response = Result<bool, EntitlementError>.Success(true);
        Assert.True((await contributor.GrantsAsync(feature, tenant, CancellationToken.None)).Value);
        var entitlementError = EntitlementError.ContributorUnavailable(contributor.Name);
        contributor.Response = Result<bool, EntitlementError>.Failure(entitlementError);
        Assert.Equal(entitlementError, (await contributor.GrantsAsync(feature, tenant, CancellationToken.None)).Error);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolver.ResolveAsync(cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GrantsAsync(FakePrincipals.System, tenant, null, cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => contributor.GrantsAsync(feature, tenant, cancelled.Token));
    }

    [Fact]
    public async Task S18_3_Inspector_reads_real_writer_records_as_immutable_snapshots()
    {
        await using var host = await PlatformTestHost.CreateBuilder().StartAsync(CancellationToken.None);
        Assert.Equal(CompositionProfile.Operated, host.CompositionProfile);
        var sink = Assert.Single(host.Services.GetServices<IAuditSink>().OfType<FakeDurableAuditSink>());
        var inspector = new AuditInspector(sink);
        var before = inspector.Records;
        using var scope = host.Services.GetRequiredService<IOperationScopeFactory>()
            .Begin(TenantId.Implicit, FakePrincipals.System);
        var writer = host.Services.GetRequiredService<IAuditWriter>();
        Assert.True((await writer.WriteAsync(new AuditAction("Test.First"), null,
            AuditOutcome.Allowed, AuditClass.Recorded, CancellationToken.None)).IsSuccess);
        var snapshot = inspector.Records;
        Assert.Equal("Test.First", Assert.Single(snapshot).Action.Value);
        Assert.Empty(before);
        Assert.Throws<NotSupportedException>(() => ((IList<AuditEvent>)snapshot).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<AuditEvent>)snapshot)[0] = snapshot[0]);
        Assert.True((await writer.WriteAsync(new AuditAction("Test.Second"), null,
            AuditOutcome.Denied, AuditClass.Required, CancellationToken.None)).IsSuccess);
        Assert.Single(snapshot);
        Assert.Equal(new[] { "Test.First", "Test.Second" }, inspector.Records.Select(record => record.Action.Value));
        Assert.All(inspector.Records, record => Assert.Equal(FakePrincipals.System.Id, record.Actor));

        var methods = typeof(AuditInspector).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
        Assert.Equal("get_Records", Assert.Single(methods).Name);
        Assert.Empty(typeof(AuditInspector).GetFields(BindingFlags.Public | BindingFlags.Instance));
        Assert.False(typeof(IAuditSink).IsAssignableFrom(typeof(AuditInspector)));
        Assert.Throws<ArgumentNullException>(() => new AuditInspector(null!));
    }
}
