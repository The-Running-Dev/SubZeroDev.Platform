using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Hosting;

var builder = WebApplication.CreateBuilder(args);

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

app.Run();

return 0;

/// <summary>The permission name this sample's own endpoint declares.</summary>
internal static class LocalSamplePermissions
{
    /// <summary>Reads the sample's root diagnostic response.</summary>
    public static PermissionName ReadRoot { get; } = new("Sample.Local.ReadRoot");
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
