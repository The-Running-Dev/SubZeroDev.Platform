using SubZeroDev.Platform.Abstractions;

namespace SubZeroDev.Platform.Core;

/// <summary>A rejected permission provider registration.</summary>
public sealed record PermissionProviderRegistrationError : PlatformError
{
    private PermissionProviderRegistrationError(string code, string detail)
        : base(code) => Detail = detail;

    /// <summary>Names the providers involved.</summary>
    public string Detail { get; }

    /// <inheritdoc/>
    public override bool IsRetryable => false;

    /// <summary>Two providers share a name — a decision naming its source would be worthless if two
    /// sources shared one.</summary>
    /// <param name="name">The duplicated name.</param>
    /// <param name="first">The provider already registered under that name.</param>
    /// <param name="second">The provider that tried to register alongside it.</param>
    /// <returns>The error.</returns>
    public static PermissionProviderRegistrationError DuplicateProviderName(
        PermissionProviderName name, string first, string second) =>
        new(
            nameof(DuplicateProviderName),
            $"Two permission providers are registered under the name '{name}': '{first}' and '{second}'.");

    /// <summary>Registration was attempted after the registry was frozen.</summary>
    /// <param name="name">The provider that arrived late.</param>
    /// <returns>The error.</returns>
    public static PermissionProviderRegistrationError RegistryFrozen(PermissionProviderName name) =>
        new(nameof(RegistryFrozen), $"The permission provider registry is frozen; '{name}' cannot be registered.");
}

/// <summary>A rejected permission catalog registration, or a name no catalog declares.</summary>
public sealed record PermissionCatalogError : PlatformError
{
    private PermissionCatalogError(string code, string detail)
        : base(code) => Detail = detail;

    /// <summary>Names the catalogs or the permission involved.</summary>
    public string Detail { get; }

    /// <inheritdoc/>
    public override bool IsRetryable => false;

    /// <summary>Two catalogs declare the same permission name — a composition defect, not a
    /// last-writer-wins.</summary>
    /// <param name="name">The duplicated name.</param>
    /// <param name="first">The catalog that already declared it.</param>
    /// <param name="second">The catalog that tried to declare it again.</param>
    /// <returns>The error.</returns>
    public static PermissionCatalogError DuplicatePermissionName(PermissionName name, string first, string second) =>
        new(
            nameof(DuplicatePermissionName),
            $"Permission name '{name}' is declared by both '{first}' and '{second}'.");

    /// <summary>A permission name reaching a registration is not declared by any registered catalog —
    /// a startup-detectable defect, never a runtime denial.</summary>
    /// <param name="name">The undeclared name.</param>
    /// <returns>The error.</returns>
    public static PermissionCatalogError UnregisteredPermission(PermissionName name) =>
        new(
            nameof(UnregisteredPermission),
            $"Permission name '{name}' is not declared by any registered permission catalog.");
}

/// <summary>Collects permission provider registrations.</summary>
public interface IPermissionProviderRegistry
{
    /// <summary>Registers one provider.</summary>
    /// <param name="provider">The provider.</param>
    /// <returns>Success, or why the registration was rejected.</returns>
    Result<PermissionProviderRegistrationError> Register(IPermissionProvider provider);

    /// <summary>Everything registered, in registration order.</summary>
    IReadOnlyList<IPermissionProvider> Registered { get; }

    /// <summary>Closes the registry. One-way: registration after this is a defect, not a
    /// condition.</summary>
    void Freeze();
}

/// <summary>Collects permission catalog registrations, and answers whether a name is declared by
/// any of them.</summary>
public interface IPermissionCatalogRegistry
{
    /// <summary>Registers one catalog's declared names.</summary>
    /// <param name="catalog">The catalog.</param>
    /// <returns>Success, or why the registration was rejected.</returns>
    Result<PermissionCatalogError> Register(IPermissionCatalog catalog);

    /// <summary>Every permission name declared by a registered catalog.</summary>
    IReadOnlyCollection<PermissionName> Declared { get; }

    /// <summary>Checks that a name is declared by some registered catalog — the primitive a
    /// registration that requires a permission name calls at startup, so a typo fails startup rather
    /// than silently denying every request.</summary>
    /// <param name="name">The name to check.</param>
    /// <returns>Success, or the name is undeclared.</returns>
    Result<PermissionCatalogError> EnsureDeclared(PermissionName name);
}

/// <inheritdoc cref="IPermissionProviderRegistry"/>
internal sealed class PermissionProviderRegistry : IPermissionProviderRegistry
{
    private readonly List<IPermissionProvider> _registered = [];
    private readonly Lock _gate = new();
    private bool _frozen;

    /// <inheritdoc/>
    public IReadOnlyList<IPermissionProvider> Registered => _registered;

    /// <inheritdoc/>
    public Result<PermissionProviderRegistrationError> Register(IPermissionProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        lock (_gate)
        {
            if (_frozen)
            {
                return Result<PermissionProviderRegistrationError>.Failure(
                    PermissionProviderRegistrationError.RegistryFrozen(provider.Name));
            }

            var existing = _registered.FirstOrDefault(registered => registered.Name == provider.Name);
            if (existing is not null)
            {
                return Result<PermissionProviderRegistrationError>.Failure(
                    PermissionProviderRegistrationError.DuplicateProviderName(
                        provider.Name, existing.GetType().Name, provider.GetType().Name));
            }

            _registered.Add(provider);
            return Result<PermissionProviderRegistrationError>.Success();
        }
    }

    /// <inheritdoc/>
    public void Freeze()
    {
        lock (_gate)
        {
            _frozen = true;
        }
    }
}

/// <inheritdoc cref="IPermissionCatalogRegistry"/>
internal sealed class PermissionCatalogRegistry : IPermissionCatalogRegistry
{
    private readonly Dictionary<PermissionName, IPermissionCatalog> _byName = [];
    private readonly Lock _gate = new();

    /// <inheritdoc/>
    public IReadOnlyCollection<PermissionName> Declared
    {
        get
        {
            lock (_gate)
            {
                return _byName.Keys.ToList();
            }
        }
    }

    /// <inheritdoc/>
    public Result<PermissionCatalogError> Register(IPermissionCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        lock (_gate)
        {
            foreach (var name in catalog.Declares)
            {
                if (_byName.TryGetValue(name, out var existing))
                {
                    return Result<PermissionCatalogError>.Failure(
                        PermissionCatalogError.DuplicatePermissionName(
                            name, existing.GetType().Name, catalog.GetType().Name));
                }
            }

            foreach (var name in catalog.Declares)
            {
                _byName[name] = catalog;
            }

            return Result<PermissionCatalogError>.Success();
        }
    }

    /// <inheritdoc/>
    public Result<PermissionCatalogError> EnsureDeclared(PermissionName name)
    {
        lock (_gate)
        {
            return _byName.ContainsKey(name)
                ? Result<PermissionCatalogError>.Success()
                : Result<PermissionCatalogError>.Failure(PermissionCatalogError.UnregisteredPermission(name));
        }
    }
}

/// <summary>Platform's own permission names — <see cref="PlatformPermissions"/> declared as a
/// catalog, so the composition profile's own consumers reach the same startup validation every
/// other module's names do.</summary>
internal sealed class PlatformPermissionCatalog : IPermissionCatalog
{
    public IReadOnlyCollection<PermissionName> Declares { get; } =
    [
        PlatformPermissions.ShareResource,
        PlatformPermissions.AdministerOrganization,
        PlatformPermissions.ReadAudit,
    ];
}

/// <summary>D5's first of exactly two permission providers, and not a role-assignment table: grants
/// every catalog-declared permission to the <see cref="PrincipalKind.System"/> principal in the
/// <see cref="CompositionProfile.Local"/> profile, nothing to <see cref="PrincipalKind.Anonymous"/>
/// in either profile, and nothing at all in <see cref="CompositionProfile.Operated"/>. Keyed to the
/// principal kind, never to the absence of a registered authentication provider — an endpoint meant
/// for an unauthenticated caller authorizes that read through its own registered permission, and
/// never inherits the local operator's trust.</summary>
internal sealed class CompositionPermissionProvider(
    PlatformOptions options, IPermissionCatalogRegistry catalogs) : IPermissionProvider
{
    public PermissionProviderName Name { get; } = new("Platform.Composition");

    public Task<Result<IReadOnlySet<PermissionName>, AuthorizationError>> GrantsAsync(
        Principal principal, TenantId tenant, ResourceRef? resource, CancellationToken cancellationToken)
    {
        IReadOnlySet<PermissionName> granted =
            options.CompositionProfile == CompositionProfile.Local && principal.Kind == PrincipalKind.System
                ? catalogs.Declared.ToHashSet()
                : new HashSet<PermissionName>();

        return Task.FromResult(Result<IReadOnlySet<PermissionName>, AuthorizationError>.Success(granted));
    }
}

/// <inheritdoc cref="IAuthorizationEvaluator"/>
/// <remarks>Asks every registered provider and takes the union. A provider that errors contributes
/// nothing and does not fail the evaluation — the union proceeds with whatever the others
/// answered. Audits exactly one <see cref="AuditClass.Required"/> record on a denial; an allowed
/// decision is not itself an audited fact.</remarks>
internal sealed class AuthorizationEvaluator(
    IPermissionProviderRegistry providers,
    ICurrentPrincipal principal,
    ICurrentTenant tenant,
    IAuditWriter auditWriter) : IAuthorizationEvaluator
{
    public async Task<AuthorizationDecision> EvaluateAsync(
        PermissionName permission, ResourceRef? resource, CancellationToken cancellationToken)
    {
        var currentPrincipal = principal.Current;
        var currentTenant = tenant.Current;
        var sources = new List<PermissionProviderName>();

        foreach (var provider in providers.Registered)
        {
            var granted = await provider
                .GrantsAsync(currentPrincipal, currentTenant, resource, cancellationToken)
                .ConfigureAwait(false);

            if (granted.IsSuccess && granted.Value.Contains(permission))
            {
                sources.Add(provider.Name);
            }
        }

        var outcome = sources.Count > 0 ? AuthorizationOutcome.Allowed : AuthorizationOutcome.Denied;

        if (outcome == AuthorizationOutcome.Denied)
        {
            await auditWriter
                .WriteAsync(
                    PlatformAuditActions.AuthorizationDenied,
                    resource,
                    AuditOutcome.Denied,
                    AuditClass.Required,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return new AuthorizationDecision(permission, resource, currentTenant, outcome, sources);
    }
}
