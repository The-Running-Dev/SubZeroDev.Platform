using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;

namespace SubZeroDev.Platform.Hosting;

/// <summary>What an endpoint requires of the fixed order's authorize and entitlement steps
/// (D5-S8, <c>20-contract.md</c> § <em>Public surface</em> 11). <c>RequiredPermission</c> is not
/// optional — an endpoint reachable by an unauthenticated caller declares one too, on the same
/// terms Mcp's tool declaration and the composition provider already state; there is no route by
/// which an endpoint may skip step 4. <c>RequiredFeature</c> is optional and must not acquire a
/// default: null means the endpoint admits no new paid-feature work, and that is a fact somebody
/// wrote down rather than the outcome of an omission.</summary>
/// <param name="RequiredPermission">The permission checked at step 4. Must be declared by a
/// registered <see cref="IPermissionCatalog"/>.</param>
/// <param name="RequiredFeature">The feature checked at step 5, or <see langword="null"/> when the
/// endpoint admits no new paid-feature work — an endpoint that reads, lists or exports data a
/// tenant already has declares none, even when that data was produced under the same feature.</param>
public sealed record EndpointRequirement(PermissionName RequiredPermission, FeatureName? RequiredFeature);

/// <summary>Why an endpoint stands outside steps 4 and 5. The reason is not optional: an exemption
/// list nobody can read is an ungated surface with an extra step, and the probes are the only
/// thing in Platform that carries one.</summary>
/// <param name="Reason">Why this endpoint is exempt.</param>
public sealed record EndpointRequirementExemption(string Reason)
{
    /// <summary>Why this endpoint is exempt.</summary>
    public string Reason { get; } = string.IsNullOrWhiteSpace(Reason)
        ? throw new ArgumentException("An exemption must state a reason.", nameof(Reason))
        : Reason;
}

/// <summary>The two conventions that attach an endpoint's declaration. Read by
/// <see cref="EndpointAuthorizationFilter"/>, by the startup check that enforces I-R6, and by
/// nothing else — a handler that branched on its own endpoint's metadata would reintroduce the
/// per-handler ordering the fixed order exists to remove.</summary>
public static class PlatformEndpointConventions
{
    /// <summary>Declares what steps 4 and 5 require of this endpoint, and attaches the filter that
    /// enforces it. An <c>IStartupFilter</c>'s own middleware always runs before the framework's
    /// implicit routing match and endpoint dispatch — there is no seam between the two it can
    /// occupy — so enforcement attaches here instead, at the endpoint itself, through
    /// <see cref="IEndpointFilter"/>: the framework's own supported point for logic that runs after
    /// routing has resolved the endpoint and before its handler.</summary>
    /// <typeparam name="TBuilder">The convention builder's type, so the call chains.</typeparam>
    /// <param name="builder">The endpoint's convention builder.</param>
    /// <param name="permission">The permission required at step 4.</param>
    /// <param name="feature">The feature required at step 5, or <see langword="null"/> when this
    /// endpoint admits no new paid-feature work.</param>
    /// <returns>The same builder, so calls chain.</returns>
    public static TBuilder RequiresPlatformAuthorization<TBuilder>(
        this TBuilder builder,
        PermissionName permission,
        FeatureName? feature)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Add(endpoint => endpoint.Metadata.Add(new EndpointRequirement(permission, feature)));
        builder.AddEndpointFilter(new EndpointAuthorizationFilter(permission, feature));
        return builder;
    }

    /// <summary>Exempts this endpoint from steps 4 and 5. Reserved for the probes — the only thing
    /// in Platform that carries one — and for an endpoint the standard registration did not map.</summary>
    /// <typeparam name="TBuilder">The convention builder's type, so the call chains.</typeparam>
    /// <param name="builder">The endpoint's convention builder.</param>
    /// <param name="reason">Why this endpoint stands outside the fixed order's checked steps. Not
    /// optional: an exemption list nobody can read is an ungated surface with an extra step.</param>
    /// <returns>The same builder, so calls chain.</returns>
    public static TBuilder ExemptFromPlatformAuthorization<TBuilder>(
        this TBuilder builder,
        string reason)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Add(endpoint => endpoint.Metadata.Add(new EndpointRequirementExemption(reason)));
        return builder;
    }
}

/// <summary>Runs steps 4 and 5 of the fixed order — authorize, then check entitlement — for one
/// endpoint's declared <see cref="EndpointRequirement"/>. Attached by
/// <see cref="PlatformEndpointConventions.RequiresPlatformAuthorization{TBuilder}"/> at the point
/// the requirement is declared, so it runs with the endpoint already resolved regardless of where
/// the fixed order's earlier steps sit in the pipeline.</summary>
internal sealed class EndpointAuthorizationFilter(PermissionName permission, FeatureName? feature) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var authorization = http.RequestServices.GetRequiredService<IAuthorizationEvaluator>();
        var correlation = http.RequestServices.GetRequiredService<ICurrentCorrelation>();

        // Step 4 — authorize. Never resource-scoped at the pipeline (I-R7): an endpoint whose
        // authorization is genuinely per-resource makes its own, second, explicit EvaluateAsync
        // call with the reference it constructed.
        var decision = await authorization
            .EvaluateAsync(permission, null, http.RequestAborted)
            .ConfigureAwait(false);

        if (decision.Outcome != AuthorizationOutcome.Allowed)
        {
            // Authorization precedes entitlement, and a principal who may not perform an action
            // must not learn from the response whether the deployment is entitled to the feature —
            // reversing them turns every entitlement into an unauthenticated probe. Refusing here,
            // before entitlement is ever asked, is what keeps that true.
            await WriteRefusalAsync(
                http, nameof(AuthorizationError.PermissionDenied), correlation, StatusCodes.Status403Forbidden)
                .ConfigureAwait(false);
            return Results.Empty;
        }

        // Step 5 — check entitlement, only when the endpoint admits new paid-feature work. An
        // endpoint that declares no feature runs no entitlement evaluation at all, even for data
        // produced under a gated feature: entitlement was checked at the admission that produced
        // it, and a lapsed licence does not re-ask a question access already answered.
        if (feature is { } requiredFeature)
        {
            var entitlement = http.RequestServices.GetRequiredService<IEntitlementEvaluator>();
            var entitlementDecision = await entitlement
                .EvaluateAsync(requiredFeature, http.RequestAborted)
                .ConfigureAwait(false);

            if (!entitlementDecision.Granted)
            {
                await WriteRefusalAsync(
                    http, nameof(EntitlementError.FeatureNotEntitled), correlation, StatusCodes.Status402PaymentRequired)
                    .ConfigureAwait(false);
                return Results.Empty;
            }
        }

        return await next(context).ConfigureAwait(false);
    }

    private static Task WriteRefusalAsync(
        HttpContext http, string code, ICurrentCorrelation correlation, int statusCode) =>
        ProbeBody.WriteAsync(http, new ErrorEnvelope(code, correlation.Current), statusCode);
}
