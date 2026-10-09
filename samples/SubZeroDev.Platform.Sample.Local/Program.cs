using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Hosting;
using SubZeroDev.Platform.Persistence;
using SubZeroDev.Platform.RuntimeSettings;

var builder = WebApplication.CreateBuilder(args);

// S45: runtime settings are an optional module a self-hosted installation can compose. It owns a
// migration, so it goes on before the migrate-mode branch below, which exits before
// AddPlatformWebHost runs; the catalogue declares the one setting this sample reads and changes.
builder.Services.AddSingleton<IPlatformModule, RuntimeSettingsModule>();
builder.Services.AddSingleton<ISettingCatalog, LocalSampleSettingCatalog>();

// Migrate mode is a one-shot command, not a third host role: it creates the settings table, and
// never serves HTTP or probes.
if (args is ["migrate"])
{
    return await builder.RunPlatformMigrateModeAsync(CancellationToken.None);
}

// D5-S8: the endpoint mapped below carries a permission declaration, checked at startup (I-R6). No
// real policy provider exists yet — Organizations, which ships the second of D5's exactly two, is
// S10 — and Local forbids every commercial registration outright (I-C3), so this sample grants its
// own declared permission to any principal: the smallest honest thing that satisfies the rule
// without pretending a policy has been decided.
builder.Services.AddSingleton<IPermissionCatalog, LocalSamplePermissionCatalog>();
builder.Services.AddSingleton<IPermissionProvider, NoPolicyPermissionProvider>();

// The identity-free deployment: no Identity, Organizations, Billing or Licensing package or
// project reference exists anywhere in this sample — see the .csproj. The only mandatory
// Platform call. Health, readiness and correlation come with it.
builder.AddPlatformWebHost();

// The settings module stores its rows through Persistence, which adds the database and
// pending-migration readiness checks.
builder.Services.AddPlatformPersistence();

var app = builder.Build();

// D5-S1.1: an adopter must be able to see, from the log alone, which shape this host claims to be.
app.Logger.LogInformation(
    "Composition profile: {CompositionProfile}",
    app.Services.GetRequiredService<PlatformOptions>().CompositionProfile);

app.MapGet("/", (ICurrentCorrelation correlation, ICurrentTenant tenant) => new
{
    correlation = correlation.Current.TraceId,
    tenant = tenant.Current.Value,
}).RequiresPlatformAuthorization(LocalSamplePermissions.ReadRoot, feature: null);

await app.StartAsync();

// S45.3: the installation changes its own global setting as system:local — the principal Local
// grants every declared permission to — and prints what a read then returns. A failure is printed
// rather than thrown: the host keeps serving either way.
using (app.Services.GetRequiredService<IOperationScopeFactory>().Begin(TenantId.Implicit, Principal.LocalSystem))
{
    var setting = LocalSampleSettings.MaxConcurrentRuns;
    var set = await app.Services.GetRequiredService<ISettingWriter>()
        .SetAsync(setting, SettingLayer.Global, 8, CancellationToken.None);
    var read = await app.Services.GetRequiredService<ISettingReader>().GetAsync(setting, CancellationToken.None);

    Console.WriteLine(set.IsSuccess && read.IsSuccess
        ? $"Runtime setting {setting.Name}: {read.Value.Value} ({read.Value.From})"
        : $"Runtime setting {setting.Name} could not be changed: {(set.IsSuccess ? read.Error.Code : set.Error.Code)}");
}

await app.WaitForShutdownAsync();

return 0;

/// <summary>The permission name this sample's own endpoint declares.</summary>
internal static class LocalSamplePermissions
{
    /// <summary>Reads the sample's root diagnostic response.</summary>
    public static PermissionName ReadRoot { get; } = new("Sample.Local.ReadRoot");
}

/// <summary>The runtime setting this sample declares.</summary>
internal static class LocalSampleSettings
{
    /// <summary>How many runs may proceed at once: 4 unless changed, and at most 10.</summary>
    public static SettingDefinition<long> MaxConcurrentRuns { get; } = SettingDefinition.Integer(
        new SettingName("Sample.Local.MaxConcurrentRuns"),
        4,
        new HashSet<SettingLayer> { SettingLayer.Global },
        static value => value is >= 1 and <= 10);
}

/// <summary><see cref="LocalSampleSettings"/> declared as a catalogue, checked at startup.</summary>
internal sealed class LocalSampleSettingCatalog : ISettingCatalog
{
    public IReadOnlyCollection<SettingDefinition> Declares { get; } = [LocalSampleSettings.MaxConcurrentRuns];
}

/// <summary><see cref="LocalSamplePermissions"/> declared as a catalog, so a typo here fails
/// startup the same way any module's would.</summary>
internal sealed class LocalSamplePermissionCatalog : IPermissionCatalog
{
    public IReadOnlyCollection<PermissionName> Declares { get; } = [LocalSamplePermissions.ReadRoot];
}

/// <summary>No real permission policy exists in this sample. Granting this sample's own declared
/// permission to any principal is the smallest honest thing that satisfies I-R6 without pretending
/// a policy has been decided — it is not a role-assignment table, and it is replaced rather than
/// kept once S10 lands.</summary>
internal sealed class NoPolicyPermissionProvider : IPermissionProvider
{
    private static readonly IReadOnlySet<PermissionName> Granted =
        new HashSet<PermissionName> { LocalSamplePermissions.ReadRoot };

    public PermissionProviderName Name { get; } = new("Sample.NoPolicy");

    public Task<Result<IReadOnlySet<PermissionName>, AuthorizationError>> GrantsAsync(
        Principal principal, TenantId tenant, ResourceRef? resource, CancellationToken cancellationToken) =>
        Task.FromResult(Result<IReadOnlySet<PermissionName>, AuthorizationError>.Success(Granted));
}
