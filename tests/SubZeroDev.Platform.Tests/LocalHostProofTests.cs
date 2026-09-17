using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Testing;

namespace SubZeroDev.Platform.Tests;

/// <summary>S17.6: the local host's own scenarios prove, directly, that it carries no reference or
/// configuration for a commercial module and that its self-originated work audits under the
/// <c>system:local</c> actor — rather than these being inferred from <c>PackageGraphTests</c>'
/// binary-level check (I-C5) or from the sample's own comments.</summary>
public sealed class LocalHostProofTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot(AppContext.BaseDirectory);

    private static readonly string[] CommercialModuleNames =
        ["Identity", "Organizations", "Billing", "Licensing"];

    [Fact]
    public void S17_6_the_local_sample_project_file_declares_no_reference_to_a_commercial_module()
    {
        var projectFile = Path.Combine(
            RepositoryRoot, "samples", "SubZeroDev.Platform.Sample.Local", "SubZeroDev.Platform.Sample.Local.csproj");
        Assert.True(File.Exists(projectFile), $"'{projectFile}' does not exist.");

        var content = File.ReadAllText(projectFile);

        foreach (var forbidden in CommercialModuleNames)
        {
            Assert.DoesNotContain($"SubZeroDev.Platform.{forbidden}", content, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void S17_6_the_local_sample_configuration_declares_no_section_for_a_commercial_module()
    {
        var appsettings = Path.Combine(
            RepositoryRoot, "samples", "SubZeroDev.Platform.Sample.Local", "appsettings.json");
        Assert.True(File.Exists(appsettings), $"'{appsettings}' does not exist.");

        var content = File.ReadAllText(appsettings);

        foreach (var forbidden in CommercialModuleNames)
        {
            Assert.DoesNotContain(forbidden, content, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task S17_6_the_local_hosts_own_scenarios_audit_under_the_system_local_actor()
    {
        var sink = new RecordingAuditSink();
        await using var host = await PlatformTestHost.CreateBuilder()
            .WithSetting("CompositionProfile", nameof(CompositionProfile.Local))
            .WithServices(services => services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IAuditSink>(sink)))
            .StartAsync(CancellationToken.None);

        var scopeFactory = host.Services.GetRequiredService<IOperationScopeFactory>();
        var writer = host.Services.GetRequiredService<IAuditWriter>();

        // Principal.LocalSystem is the actor Platform's own doc comment names for "a request the
        // host itself originates" (Principal.cs) — the shape every existing self-originated write
        // (OutboxDispatcher, LicensingVerifier) already uses, exercised here under the Local profile
        // specifically rather than the default Operated one.
        using (scopeFactory.Begin(TenantId.Implicit, Principal.LocalSystem))
        {
            var written = await writer.WriteAsync(
                new AuditAction("sample.local.self-check"), null, AuditOutcome.Allowed, AuditClass.Recorded,
                CancellationToken.None);
            Assert.True(written.IsSuccess);
        }

        var recorded = Assert.Single(sink.Received);
        Assert.Equal(PrincipalId.LocalSystem, recorded.Actor);
        Assert.Equal(PrincipalKind.System, recorded.ActorKind);
        Assert.Equal("system:local", recorded.Actor.ToString());
    }

    private static string FindRepositoryRoot(string start)
    {
        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SubZeroDev.Platform.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException($"Could not locate the repository root above '{start}'.");
    }
}
