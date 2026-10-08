using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Billing;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Licensing;
using SubZeroDev.Platform.Mcp;
using SubZeroDev.Platform.Organizations;
using SubZeroDev.Platform.Persistence;
using SubZeroDev.Platform.RuntimeSettings;
using SubZeroDev.Platform.Testing;

namespace SubZeroDev.Platform.Tests;

/// <summary>S28.2 (I-U6): a secret and a payload pushed through every audited input surface reach
/// neither the stored record nor any log line. The surfaces are not a list kept by hand: they are
/// every type in a <c>src/*</c> package whose code calls <see cref="IAuditWriter.WriteAsync"/>, read
/// from the compiled IL, plus the writer itself as the surface a consumer calls. A surface added
/// later without a driver here fails <see cref="Every_audited_input_surface_has_a_driver"/>.</summary>
/// <remarks>Logs are read back from the host's real JSONL file sink rather than from a test logger
/// provider: Platform composes Serilog with <c>writeToProviders: false</c>, so a provider would see
/// nothing, and the file is what an operator reads. The class runs in the serial telemetry
/// collection because Serilog's static logger is process-wide: a host disposed concurrently by
/// another test closes it, and this host's lines would go nowhere.</remarks>
[Collection(TelemetryTestCollection.Name)]
public sealed class AuditInputSurfaceTests
{
    private const string Secret = "ghp_1234567890abcdef1234567890abcdef1234";
    private const string Payload = "s28-payload-marker-7f3c";

    private static readonly Principal Alice = new(new PrincipalId("issuer-a", "alice"), PrincipalKind.Account, "Alice", null);
    private static readonly Principal Bob = new(new PrincipalId("issuer-a", "bob"), PrincipalKind.Account, "Bob", null);

    /// <summary>One driver per surface, keyed by the surface's full type name.</summary>
    private static readonly IReadOnlyDictionary<string, Func<Probe, Task>> Drivers = new Dictionary<string, Func<Probe, Task>>
    {
        [typeof(IAuditWriter).FullName!] = DriveAuditWriterAsync,
        ["SubZeroDev.Platform.Core.AuthorizationEvaluator"] = DriveAuthorizationEvaluatorAsync,
        ["SubZeroDev.Platform.Billing.BillingApi"] = DriveBillingAsync,
        ["SubZeroDev.Platform.Licensing.LicenceVerifier"] = DriveLicensingAsync,
        ["SubZeroDev.Platform.Mcp.PlatformMcpTool"] = DriveMcpToolAsync,
        ["SubZeroDev.Platform.Organizations.OrganizationApi"] = DriveOrganizationsAsync,
        ["SubZeroDev.Platform.Persistence.SharedReadScopeFactory"] = DriveSharedReadAsync,
        ["SubZeroDev.Platform.RuntimeSettings.SettingWriter"] = DriveSettingWriterAsync,
    };

    public static TheoryData<string> Surfaces
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var surface in Drivers.Keys.Order(StringComparer.Ordinal))
            {
                data.Add(surface);
            }

            return data;
        }
    }

    [Fact]
    public void Every_audited_input_surface_has_a_driver()
    {
        var enumerated = AuditWriteCallers().Append(typeof(IAuditWriter).FullName!).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(
            enumerated.Order(StringComparer.Ordinal),
            Drivers.Keys.Order(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Surfaces))]
    public async Task S28_2_A_secret_and_a_payload_reach_neither_the_record_nor_a_log_line(string surface)
    {
        var probe = new Probe();
        try
        {
            await Drivers[surface](probe);

            var received = probe.Sink.Received;
            Assert.NotEmpty(received);

            var log = await probe.ReadLogAsync();
            foreach (var forbidden in probe.Forbidden)
            {
                foreach (var record in received)
                {
                    var rendered = string.Join(
                        "|", record.Action.Value, record.Resource?.Type, record.Resource?.Id, record.Actor, record);
                    Assert.DoesNotContain(forbidden, rendered, StringComparison.Ordinal);
                }

                Assert.DoesNotContain(forbidden, log, StringComparison.Ordinal);
            }
        }
        finally
        {
            probe.Dispose();
        }
    }

    // Drivers ------------------------------------------------------------------------------------

    private static async Task DriveAuditWriterAsync(Probe probe)
    {
        await using var host = await StartHostAsync(probe);
        var scopes = host.Services.GetRequiredService<IOperationScopeFactory>();
        var writer = host.Services.GetRequiredService<IAuditWriter>();

        using (scopes.Begin(TenantId.Implicit, Alice))
        {
            // The writer has no payload parameter at all (I-U1); the resource is the input it takes.
            await writer.WriteAsync(
                new AuditAction("Surface.Probe"),
                new ResourceRef(Secret, Secret),
                AuditOutcome.Allowed,
                AuditClass.Recorded,
                CancellationToken.None);
        }

        await probe.SealAsync(host.Services);
    }

    private static async Task DriveAuthorizationEvaluatorAsync(Probe probe)
    {
        var permission = new PermissionName("Surface.Probe.Use");
        await using var host = await StartHostAsync(
            probe, services => services.AddSingleton<IPermissionCatalog>(new StubPermissionCatalog(permission)));
        var scopes = host.Services.GetRequiredService<IOperationScopeFactory>();
        var evaluator = host.Services.GetRequiredService<IAuthorizationEvaluator>();

        using (scopes.Begin(TenantId.Implicit, Alice))
        {
            var decision = await evaluator.EvaluateAsync(permission, new ResourceRef(Secret, Secret), CancellationToken.None);
            Assert.Equal(AuthorizationOutcome.Denied, decision.Outcome);
        }

        await probe.SealAsync(host.Services);
    }

    private static async Task DriveBillingAsync(Probe probe)
    {
        var operatorPrincipal = new Principal(
            new PrincipalId("issuer-billing", "operator"), PrincipalKind.Account, "Operator", null);
        var pro = new PlanKey("pro");

        await using var host = await StartHostAsync(probe, services => services.AddSingleton<IPlatformModule, BillingModule>());
        var api = host.Services.GetRequiredService<IBillingApi>();
        var scopes = host.Services.GetRequiredService<IOperationScopeFactory>();
        await api.RegisterPlanAsync(pro, "Pro", new HashSet<FeatureName> { new("premium-reports") }, CancellationToken.None);

        var transitioned = new TenantId(Guid.NewGuid());
        using (scopes.Begin(transitioned, operatorPrincipal))
        {
            var result = await api.TransitionAsync(
                transitioned, pro, SubscriptionState.Active, host.Clock.UtcNow, host.Clock.UtcNow.AddDays(30),
                trialEndsAt: null, providerReference: Payload, CancellationToken.None);
            Assert.True(result.IsSuccess);
        }

        var evented = new TenantId(Guid.NewGuid());
        var providerEvent = new ProviderEvent(
            "acme-pay", Secret, evented, pro, SubscriptionState.Active,
            host.Clock.UtcNow, host.Clock.UtcNow.AddDays(30), null, Payload);
        using (scopes.Begin(evented, operatorPrincipal))
        {
            // The second presentation is the duplicate path, which logs the event it skipped.
            Assert.True((await api.HandleProviderEventAsync(providerEvent, CancellationToken.None)).IsSuccess);
            Assert.True((await api.HandleProviderEventAsync(providerEvent, CancellationToken.None)).IsSuccess);
        }

        await probe.SealAsync(host.Services);
    }

    private static async Task DriveLicensingAsync(Probe probe)
    {
        using var signer = new LicensingTests.TestLicenceSigner("s28-key");
        var issuedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var document = Path.Combine(probe.LogDirectory, "licence.json");
        File.WriteAllBytes(
            document,
            signer.Mint(Secret, [new FeatureName(Payload)], issuedAt, issuedAt.AddDays(365), graceDays: 30, tamper: false));

        await using var host = await StartHostAsync(
            probe,
            services =>
            {
                services.AddSingleton<IPlatformModule, LicensingModule>();
                services.AddSingleton(signer.Options(document));

                // Verification is driven below, after migrations, as LicensingTests does.
                foreach (var startup in services.Where(d => d.ImplementationType == typeof(LicenceStartupVerification)).ToList())
                {
                    services.Remove(startup);
                }
            });

        var outcome = await host.Services.GetRequiredService<ILicenceVerifier>().VerifyOnceAsync(CancellationToken.None);
        Assert.True(outcome.IsSuccess);

        await probe.SealAsync(host.Services);
    }

    private static async Task DriveMcpToolAsync(Probe probe)
    {
        var settings = new Dictionary<string, string?> { ["Platform:Telemetry:LogDirectory"] = probe.LogDirectory };

        foreach (var grants in new[] { true, false })
        {
            var invoker = new McpInvocationTests.StubToolInvoker("producer", (_, _) => ToolInvocationResult.Success("ok"));
            await using var harness = await McpInvocationTests.Harness.StartAsync(
                exposed: [new ToolName("store-doc")],
                extra: [
                    new StubToolProducer("producer",
                        new ToolDefinition(
                            new ToolName("store-doc"),
                            "Stores.",
                            McpInvocationTests.Schema("resourceId", "body"),
                            McpInvocationTests.UsePermission,
                            null)),
                    invoker,
                ],
                grantsPermission: grants,
                audit: probe.Sink,
                settings: settings);

            await using (var client = await harness.ConnectAsync("alice"))
            {
                await client.CallToolAsync(
                    "store-doc",
                    new Dictionary<string, object?> { ["resourceId"] = Secret, ["body"] = Payload },
                    cancellationToken: CancellationToken.None);
            }

            Assert.Equal(grants ? 1 : 0, invoker.CallCount);
            await probe.SealAsync(harness.Services);
        }
    }

    private static async Task DriveOrganizationsAsync(Probe probe)
    {
        await using var host = await StartHostAsync(probe, services => services.AddSingleton<IPlatformModule, OrganizationsModule>());
        var api = host.Services.GetRequiredService<IOrganizationApi>();
        var scopes = host.Services.GetRequiredService<IOperationScopeFactory>();

        OrganizationId organization;
        string token;
        using (scopes.Begin(TenantId.Implicit, Alice))
        {
            organization = (await api.CreateOrganizationAsync(Payload, CancellationToken.None)).Value.Id;
            token = (await api.InviteAsync(
                organization, OrganizationRole.Member, host.Clock.UtcNow.AddDays(7), CancellationToken.None)).Value.Token;
        }

        // A minted invitation token is a bearer secret in its own right.
        probe.Forbidden.Add(token);

        using (scopes.Begin(TenantId.Implicit, Bob))
        {
            Assert.True((await api.RedeemInvitationAsync(token, CancellationToken.None)).IsSuccess);
            Assert.False((await api.RedeemInvitationAsync(Secret, CancellationToken.None)).IsSuccess);
        }

        using (scopes.Begin(TenantId.Implicit, Alice))
        {
            Assert.True((await api.RevokeMembershipAsync(organization, Bob.Id, CancellationToken.None)).IsSuccess);
        }

        await probe.SealAsync(host.Services);
    }

    private static async Task DriveSharedReadAsync(Probe probe)
    {
        await using var host = await StartHostAsync(probe);
        var scopes = host.Services.GetRequiredService<IOperationScopeFactory>();
        var factory = host.Services.GetRequiredService<ISharedReadScopeFactory>();

        // Opening a scope takes no caller value: the type argument is the whole input.
        using (scopes.Begin(TenantId.Implicit, Alice))
        using (factory.Open<SurfaceRow>().Value)
        {
        }

        await probe.SealAsync(host.Services);
    }

    private static async Task DriveSettingWriterAsync(Probe probe)
    {
        await using var host = await StartHostAsync(probe, services =>
        {
            services.AddSingleton<IPlatformModule, RuntimeSettingsModule>();
            services.AddSingleton<ISettingCatalog>(new RuntimeSettingsTests.SampleCatalog(RuntimeSettingsTests.Banner));
        });
        var writer = host.Services.GetRequiredService<ISettingWriter>();
        var scopes = host.Services.GetRequiredService<IOperationScopeFactory>();

        // The value is the input a write takes (I-ST4): set, replaced, cleared, and refused at a layer
        // Alice holds no permission for. S44.1 names its own sentinel value.
        const string sentinel = "SENTINEL-7f3a";
        probe.Forbidden.Add(sentinel);
        using (scopes.Begin(RuntimeSettingsTests.T1, Alice))
        {
            var banner = RuntimeSettingsTests.Banner;
            Assert.True((await writer.SetAsync(banner, SettingLayer.User, sentinel, CancellationToken.None)).IsSuccess);
            Assert.True((await writer.SetAsync(banner, SettingLayer.User, Secret, CancellationToken.None)).IsSuccess);
            Assert.True((await writer.SetAsync(banner, SettingLayer.User, Payload, CancellationToken.None)).IsSuccess);
            Assert.True((await writer.ClearAsync(banner, SettingLayer.User, CancellationToken.None)).IsSuccess);
            Assert.False((await writer.SetAsync(banner, SettingLayer.Global, Secret, CancellationToken.None)).IsSuccess);
        }

        await probe.SealAsync(host.Services);
    }

    private static async Task<IPlatformTestHost> StartHostAsync(Probe probe, Action<IServiceCollection>? extra = null)
    {
        var host = await PlatformTestHost.CreateBuilder()
            .WithProvider(PersistenceProvider.Sqlite)
            .WithSetting("Telemetry:LogDirectory", probe.LogDirectory)
            .WithServices(services =>
            {
                services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditSink>(probe.Sink));
                extra?.Invoke(services);
            })
            .StartAsync(CancellationToken.None);

        var migrated = await host.Services.GetRequiredService<IMigrationRunner>().ApplyAsync(CancellationToken.None);
        Assert.True(migrated.IsSuccess);
        return host;
    }

    private sealed record SurfaceRow(TenantId Tenant, string LogicalId, DateTimeOffset? SharedAt) : IShareable;

    /// <summary>One driver's evidence: the sink every record reaches, and the directory the host's
    /// file sink writes to. Each host a driver starts logs one sentinel line last, so reading the
    /// file until every sentinel is present proves everything logged before it has been flushed.</summary>
    internal sealed class Probe : IDisposable
    {
        private readonly string _sentinel = $"s28-sentinel-{Guid.NewGuid():N}";
        private int _seals;

        internal string LogDirectory { get; } = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"platform-s28-{Guid.NewGuid():N}")).FullName;

        internal RecordingAuditSink Sink { get; } = new("s28-surface", isDurable: true);

        internal List<string> Forbidden { get; } = [Secret, Payload];

        internal Task SealAsync(IServiceProvider services)
        {
            services.GetRequiredService<ILoggerFactory>().CreateLogger("S28.Probe").LogWarning("Probe sealed: {Sentinel}", _sentinel);
            _seals++;
            return Task.CompletedTask;
        }

        internal async Task<string> ReadLogAsync()
        {
            for (var attempt = 0; attempt < 150; attempt++)
            {
                var text = string.Concat(await Task.WhenAll(
                    Directory.GetFiles(LogDirectory, "*.jsonl").Select(ReadSharedAsync)));
                if (Occurrences(text, _sentinel) >= _seals)
                {
                    return text;
                }

                await Task.Delay(100);
            }

            throw new TimeoutException($"The host's log file never received all {_seals} sentinel line(s).");
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(LogDirectory, recursive: true);
            }
            catch (IOException)
            {
                // A file sink still closing; the temp directory is left behind.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static async Task<string> ReadSharedAsync(string path)
        {
            try
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return await reader.ReadToEndAsync();
            }
            catch (IOException)
            {
                return string.Empty;
            }
        }

        private static int Occurrences(string text, string value)
        {
            var count = 0;
            for (var index = text.IndexOf(value, StringComparison.Ordinal);
                 index >= 0;
                 index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
            {
                count++;
            }

            return count;
        }
    }

    // Enumeration --------------------------------------------------------------------------------

    private static readonly IReadOnlyDictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(opCode => opCode.Value);

    /// <summary>Every non-compiler-generated type, in every package under <c>src/</c>, whose IL
    /// calls <see cref="IAuditWriter.WriteAsync"/> — excluding the writer's own implementations,
    /// which forward a call rather than originate one.</summary>
    private static IReadOnlySet<string> AuditWriteCallers()
    {
        var root = S17MutationFixtures.FindRepositoryRoot(AppContext.BaseDirectory);
        var packages = Directory.GetDirectories(Path.Combine(root, "src"))
            .Select(Path.GetFileName)
            .Where(name => File.Exists(Path.Combine(root, "src", name!, name + ".csproj")))
            .ToList();
        Assert.NotEmpty(packages);

        var callers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var package in packages)
        {
            var assembly = Assembly.Load(new AssemblyName(package!));
            foreach (var type in LoadableTypes(assembly))
            {
                const BindingFlags all = BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic
                    | BindingFlags.Instance | BindingFlags.Static;
                var methods = type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all));
                if (!methods.Any(CallsAuditWrite))
                {
                    continue;
                }

                var surface = OriginatingType(type);
                if (!typeof(IAuditWriter).IsAssignableFrom(surface))
                {
                    callers.Add(surface.FullName!);
                }
            }
        }

        return callers;
    }

    private static IEnumerable<Type> LoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.OfType<Type>();
        }
    }

    /// <summary>A lambda's closure or an async method's state machine is the type that wrote it.</summary>
    private static Type OriginatingType(Type type)
    {
        while (type.DeclaringType is not null
               && (type.Name.StartsWith('<') || type.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false)))
        {
            type = type.DeclaringType;
        }

        return type;
    }

    private static bool CallsAuditWrite(MethodBase method)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray();
        if (il is null)
        {
            return false;
        }

        var typeArguments = method.DeclaringType is { IsGenericType: true } declaring ? declaring.GetGenericArguments() : null;
        var methodArguments = method is MethodInfo { IsGenericMethod: true } generic ? generic.GetGenericArguments() : null;

        for (var offset = 0; offset < il.Length;)
        {
            short value = il[offset++];
            if (value == 0xFE)
            {
                value = unchecked((short)(0xFE00 | il[offset++]));
            }

            var opCode = OpCodesByValue[value];
            if (opCode.OperandType == OperandType.InlineMethod)
            {
                var token = BitConverter.ToInt32(il, offset);
                try
                {
                    var target = method.Module.ResolveMethod(token, typeArguments, methodArguments);
                    if (target?.DeclaringType == typeof(IAuditWriter) && target.Name == nameof(IAuditWriter.WriteAsync))
                    {
                        return true;
                    }
                }
                catch (ArgumentException)
                {
                    // A token this context cannot resolve names no IAuditWriter member.
                }
            }

            offset += opCode.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + (4 * BitConverter.ToInt32(il, offset)),
                _ => 4,
            };
        }

        return false;
    }
}
