using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Audit;
using SubZeroDev.Platform.Hosting;
using SubZeroDev.Platform.Licensing;
using SubZeroDev.Platform.Organizations;

namespace SubZeroDev.Platform.Sample.Web;

/// <summary>The four reads and the one write the administration shell calls (D5-S16), mapped as
/// ordinary endpoints on this sample host — the shell holds no privileged endpoint of its own (I-W1):
/// every route here is declared with <c>RequiresPlatformAuthorization</c>, the same way <c>/orders</c>
/// is, and any caller holding the declared permission may call it without the shell in the
/// loop.</summary>
public static class AdminEndpoints
{
    /// <summary>The feature the shell's entitlement view asks about. A sample-owned demonstration
    /// name, not a Platform well-known one — Platform declares no feature vocabulary of its
    /// own.</summary>
    public static FeatureName DemonstrationFeature { get; } = new("Sample.Web.PremiumWidget");

    /// <summary>The exact routes the shell calls (<c>shell/src/api.ts</c>), named once so the I-W1
    /// assertion (S16.3) can enumerate the live endpoint data source and confirm every one of them
    /// is mapped as an ordinary endpoint, reachable by any caller holding the declared permission —
    /// never a route only the shell knows about.</summary>
    public static readonly IReadOnlyList<(string Method, string RouteTemplate)> ShellCalls =
    [
        ("GET", "/api/me"),
        ("POST", "/api/organizations/{organizationId:guid}/switch"),
        ("GET", "/api/entitlement"),
        ("GET", "/api/audit"),
    ];

    /// <summary>Maps the shell's four reads and one write.</summary>
    public static void MapAdminEndpoints(this WebApplication app)
    {
        app.MapGet("/api/me", (ICurrentPrincipal principal) =>
            {
                var current = principal.Current;
                return Results.Ok(new PrincipalView(
                    current.Kind.ToString(),
                    current.DisplayName,
                    current.Kind != PrincipalKind.Anonymous));
            })
            .RequiresPlatformAuthorization(OperatedComposition.SamplePermissions.ReadIdentity, feature: null);

        app.MapPost("/api/organizations/{organizationId:guid}/switch", async (
                Guid organizationId,
                IOrganizationApi organizations,
                CancellationToken cancellationToken) =>
            {
                var result = await organizations.SwitchActiveOrganizationAsync(
                    new OrganizationId(organizationId), cancellationToken).ConfigureAwait(false);

                if (result.IsSuccess)
                {
                    return Results.Ok(new OrganizationSwitchView(result.Value.Value));
                }

                return result.Error.Code == nameof(OrganizationError.OrganizationNotFound)
                    ? Results.NotFound()
                    : Results.Problem(result.Error.Detail);
            })
            .RequiresPlatformAuthorization(OperatedComposition.SamplePermissions.SwitchOrganization, feature: null);

        app.MapGet("/api/entitlement", async (
                IEntitlementEvaluator entitlement,
                ILicenceState licence,
                CancellationToken cancellationToken) =>
            {
                var decision = await entitlement.EvaluateAsync(DemonstrationFeature, cancellationToken)
                    .ConfigureAwait(false);
                var claims = licence.Current;

                return Results.Ok(new EntitlementView(
                    decision.Feature.Value,
                    decision.Granted,
                    decision.Sources.Select(source => source.Value).ToArray(),
                    licence.Tier.Value,
                    claims?.ExpiresAt,
                    claims?.GraceEndsAt,
                    licence.LastOutcome?.ToString()));
            })
            .RequiresPlatformAuthorization(OperatedComposition.SamplePermissions.ReadEntitlement, feature: null);

        app.MapGet("/api/audit", async (
                [FromQuery] Guid tenant,
                [FromQuery] DateTimeOffset from,
                [FromQuery] DateTimeOffset to,
                IAuditReadApi audit,
                CancellationToken cancellationToken) =>
            {
                var result = await audit.ByTenantAsync(new TenantId(tenant), from, to, cancellationToken)
                    .ConfigureAwait(false);

                if (!result.IsSuccess)
                {
                    return Results.Problem(result.Error.Detail);
                }

                var view = result.Value.Select(record => new AuditEventView(
                    record.Id.Value,
                    record.OccurredAt,
                    record.Actor.Issuer,
                    record.Actor.Subject,
                    record.ActorKind.ToString(),
                    record.Tenant.Value,
                    record.Action.Value,
                    record.Resource?.Type,
                    record.Resource?.Id,
                    record.Outcome.ToString(),
                    record.Correlation.TraceId,
                    record.Class.ToString())).ToArray();

                return Results.Ok(view);
            })
            .RequiresPlatformAuthorization(PlatformPermissions.ReadAudit, feature: null);
    }
}

/// <summary>S16.1: the shell's identity view.</summary>
public sealed record PrincipalView(string Kind, string? DisplayName, bool IsAuthenticated);

/// <summary>S16.2: the shell's organization-switch view.</summary>
public sealed record OrganizationSwitchView(Guid Tenant);

/// <summary>S16.4: the shell's entitlement and licence view.</summary>
public sealed record EntitlementView(
    string Feature,
    bool Granted,
    IReadOnlyCollection<string> Sources,
    string LicenceTier,
    DateTimeOffset? LicenceExpiresAt,
    DateTimeOffset? LicenceGraceEndsAt,
    string? LicenceVerificationOutcome);

/// <summary>S16.5: one row of the shell's audit view.</summary>
public sealed record AuditEventView(
    Guid Id,
    DateTimeOffset OccurredAt,
    string ActorIssuer,
    string ActorSubject,
    string ActorKind,
    Guid Tenant,
    string Action,
    string? ResourceType,
    string? ResourceId,
    string Outcome,
    string Correlation,
    string Class);
