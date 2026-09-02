namespace SubZeroDev.Platform.Abstractions;

/// <summary>A stable permission id in the <c>Product.Area.Action</c> form platform-specification.md
/// fixes. Compared ordinally; never parsed, wildcarded or matched by prefix.</summary>
/// <param name="Value">The stable id.</param>
public readonly record struct PermissionName(string Value)
{
    /// <summary>The stable id.</summary>
    public string Value { get; } = Value ?? throw new ArgumentNullException(nameof(Value));

    /// <inheritdoc/>
    public override string ToString() => Value;
}

/// <summary>A registered grant contributor's name, so a decision can name which one granted.</summary>
/// <param name="Value">The provider's name.</param>
public readonly record struct PermissionProviderName(string Value)
{
    /// <summary>The provider's name.</summary>
    public string Value { get; } = Value ?? throw new ArgumentNullException(nameof(Value));

    /// <inheritdoc/>
    public override string ToString() => Value;
}

/// <summary>What an authorization check resolved to.</summary>
public enum AuthorizationOutcome
{
    /// <summary>At least one registered provider granted the permission.</summary>
    Allowed,

    /// <summary>No registered provider granted the permission.</summary>
    Denied,
}

/// <summary>One authorization check's result. <c>Sources</c> is non-empty if and only if
/// <c>Outcome</c> is <see cref="AuthorizationOutcome.Allowed"/> — a denial has no source because the
/// reason for a denial is that nothing granted, which is not a provider fact.</summary>
/// <param name="Permission">The permission checked.</param>
/// <param name="Resource">The resource the check was scoped to, when it was resource-scoped.</param>
/// <param name="Tenant">The tenant the check was evaluated in.</param>
/// <param name="Outcome">What the check resolved to.</param>
/// <param name="Sources">The providers that granted. A set: its order carries no meaning.</param>
public sealed record AuthorizationDecision(
    PermissionName Permission,
    ResourceRef? Resource,
    TenantId Tenant,
    AuthorizationOutcome Outcome,
    IReadOnlyCollection<PermissionProviderName> Sources);

/// <summary>Platform's own permission names. Public surface: a consumer's policy refers to them by
/// name.</summary>
public static class PlatformPermissions
{
    /// <summary>Publishes a row for reading by other tenants.</summary>
    public static PermissionName ShareResource { get; } = new("Platform.Tenancy.ShareResource");

    /// <summary>Administers an organization's membership.</summary>
    public static PermissionName AdministerOrganization { get; } = new("Platform.Organizations.Administer");

    /// <summary>Reads the audit trail.</summary>
    public static PermissionName ReadAudit { get; } = new("Platform.Audit.Read");
}

/// <summary>Contributes grants. The evaluator asks every registered provider and takes the
/// union.</summary>
public interface IPermissionProvider
{
    /// <summary>The provider's name, unique among registered providers.</summary>
    PermissionProviderName Name { get; }

    /// <summary>Answers which of the declared permission names this provider grants the principal, in
    /// the given tenant, against the resource when the check is resource-scoped. An error denies; it
    /// never grants — an unreachable store fails closed. Must not audit: the evaluator audits the
    /// decision once.</summary>
    /// <param name="principal">The principal being checked.</param>
    /// <param name="tenant">The tenant the check is evaluated in.</param>
    /// <param name="resource">The resource the check is scoped to, when resource-scoped.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>The permission names granted, or why this provider could not answer.</returns>
    Task<Result<IReadOnlySet<PermissionName>, AuthorizationError>> GrantsAsync(
        Principal principal,
        TenantId tenant,
        ResourceRef? resource,
        CancellationToken cancellationToken);
}

/// <summary>Declares the permission names a module contributes. Collected and frozen at
/// startup.</summary>
public interface IPermissionCatalog
{
    /// <summary>The permission names this catalog declares.</summary>
    IReadOnlyCollection<PermissionName> Declares { get; }
}

/// <summary>The single evaluator. Framework packages, modules and product code all ask through
/// it.</summary>
public interface IAuthorizationEvaluator
{
    /// <summary>Evaluates one permission against the ambient principal and tenant — neither is a
    /// parameter, so no call site can evaluate in a tenant the request did not resolve. Always
    /// returns a decision; a provider that could not answer denies rather than failing the
    /// evaluation.</summary>
    /// <param name="permission">The permission to check. Must be declared by a registered
    /// <see cref="IPermissionCatalog"/> — an undeclared name is a startup-detectable defect, never a
    /// runtime denial.</param>
    /// <param name="resource">The resource the check is scoped to, when resource-scoped.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>The decision.</returns>
    Task<AuthorizationDecision> EvaluateAsync(
        PermissionName permission,
        ResourceRef? resource,
        CancellationToken cancellationToken);
}

/// <summary>Why an authorization check did not allow the caller to proceed.</summary>
public sealed record AuthorizationError : PlatformError
{
    private AuthorizationError(string code, bool isRetryable, string detail)
        : base(code)
    {
        IsRetryable = isRetryable;
        Detail = detail;
    }

    /// <summary>Names the cause.</summary>
    public string Detail { get; }

    /// <inheritdoc/>
    public override bool IsRetryable { get; }

    /// <summary>No provider granted, and the principal can see the resource. The caller returns
    /// forbidden: existence is already known, so pretending otherwise only obscures the fix.</summary>
    /// <param name="permission">The permission that was denied.</param>
    /// <returns>The error.</returns>
    public static AuthorizationError PermissionDenied(PermissionName permission) =>
        new(nameof(PermissionDenied), isRetryable: false, $"Permission '{permission}' was denied.");

    /// <summary>The resource is in another tenant, or the principal may not know it exists. The
    /// caller returns not found — a cross-tenant read and a membership the principal lacks both
    /// answer the same way.</summary>
    /// <param name="resource">The resource that is not visible.</param>
    /// <returns>The error.</returns>
    public static AuthorizationError ResourceNotVisible(ResourceRef resource) =>
        new(
            nameof(ResourceNotVisible),
            isRetryable: false,
            $"Resource '{resource.Type}:{resource.Id}' is not visible to this principal.");

    /// <summary>A provider could not answer — typically an unreachable store. Denies for this
    /// request; the caller may retry.</summary>
    /// <param name="provider">The provider that could not answer.</param>
    /// <returns>The error.</returns>
    public static AuthorizationError ProviderUnavailable(PermissionProviderName provider) =>
        new(nameof(ProviderUnavailable), isRetryable: true, $"Permission provider '{provider}' could not answer.");
}
