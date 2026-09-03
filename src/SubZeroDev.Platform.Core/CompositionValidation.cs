using SubZeroDev.Platform.Abstractions;

namespace SubZeroDev.Platform.Core;

/// <summary>Which composition rule a host's registrations broke. Core decides; Hosting names the
/// failure, because <c>HostStartupError</c> is Hosting's and Core may not reference it.</summary>
internal enum CompositionFinding
{
    /// <summary><see cref="CompositionProfile.Operated"/> with no authentication provider
    /// registered — I-C1.</summary>
    AuthenticationProviderRequired,

    /// <summary><see cref="CompositionProfile.Operated"/> with no sink declaring
    /// <see cref="IAuditSink.IsDurable"/> — I-C2.</summary>
    DurableAuditSinkRequired,

    /// <summary><see cref="CompositionProfile.Local"/> with a registration the profile forbids —
    /// I-C3.</summary>
    RegistrationForbiddenByProfile,
}

/// <summary>One broken composition rule, with the sentence an operator acts on.</summary>
/// <param name="Finding">Which rule broke.</param>
/// <param name="Detail">The profile, the offending registration, and which of the two it disagrees
/// with — the <c>Detail</c> convention <c>ModuleGraphError</c> and <c>ConfigurationError</c> already
/// follow.</param>
internal sealed record CompositionViolation(CompositionFinding Finding, string Detail);

/// <summary>Validates a host's frozen registrations against the composition profile it declared.</summary>
/// <remarks>The package graph is already checked by the architecture tests, and this is deliberately
/// not that check: the graph is a build-time fact and a misconfigured host is a runtime one. An
/// operated host that started with no authentication provider would serve, and every guarantee in
/// the design is stated relative to a composition the host never announced.
///
/// <para>Every finding fails the host. None degrades it, and none is retryable — a misconfigured
/// installation does not resolve itself, and a host that cannot state its own composition should not
/// serve.</para>
///
/// <para>The first violation found is returned rather than all of them, on the precedent
/// <c>ModuleGraphError</c> sets for the module graph: an operator fixes one registration at a time,
/// and a list invites fixing the last line first.</para></remarks>
internal static class CompositionValidator
{
    /// <summary>Checks the frozen registries against the declared profile.</summary>
    /// <param name="profile">The profile the host declared.</param>
    /// <param name="authenticationProviders">The frozen authentication providers.</param>
    /// <param name="auditSinks">The frozen audit sinks.</param>
    /// <param name="tenantResolvers">The frozen tenant resolvers.</param>
    /// <param name="entitlementContributors">The frozen entitlement contributors.</param>
    /// <returns>The first violation, or <see langword="null"/> when the composition holds.</returns>
    internal static CompositionViolation? Validate(
        CompositionProfile profile,
        IReadOnlyList<IAuthenticationProvider> authenticationProviders,
        IReadOnlyList<IAuditSink> auditSinks,
        IReadOnlyList<ITenantResolver> tenantResolvers,
        IReadOnlyList<IEntitlementContributor> entitlementContributors)
    {
        ArgumentNullException.ThrowIfNull(authenticationProviders);
        ArgumentNullException.ThrowIfNull(auditSinks);
        ArgumentNullException.ThrowIfNull(tenantResolvers);
        ArgumentNullException.ThrowIfNull(entitlementContributors);

        return profile switch
        {
            CompositionProfile.Operated => ValidateOperated(authenticationProviders, auditSinks),
            CompositionProfile.Local => ValidateLocal(
                authenticationProviders, tenantResolvers, entitlementContributors),
            _ => null,
        };
    }

    private static CompositionViolation? ValidateOperated(
        IReadOnlyList<IAuthenticationProvider> authenticationProviders,
        IReadOnlyList<IAuditSink> auditSinks)
    {
        if (authenticationProviders.Count == 0)
        {
            return new CompositionViolation(
                CompositionFinding.AuthenticationProviderRequired,
                $"The host declared the '{CompositionProfile.Operated}' profile, which is authenticated "
                + "at the transport, and registered no IAuthenticationProvider. Register one, or "
                + $"declare the '{CompositionProfile.Local}' profile.");
        }

        // The default log sink declares IsDurable == false and is never an Operated fallback (I-C2).
        // Inferring durability by trying a write would make the check depend on the store being up at
        // startup, which is the one moment it is least likely to be.
        if (!auditSinks.Any(sink => sink.IsDurable))
        {
            var registered = auditSinks.Count == 0
                ? "no audit sink is registered"
                : $"the registered sinks are {Describe(auditSinks.Select(sink => sink.Name))}, and none declares it";

            return new CompositionViolation(
                CompositionFinding.DurableAuditSinkRequired,
                $"The host declared the '{CompositionProfile.Operated}' profile, which requires an "
                + $"IAuditSink declaring IsDurable, and {registered}. Register the audit store module, "
                + $"or declare the '{CompositionProfile.Local}' profile.");
        }

        return null;
    }

    private static CompositionViolation? ValidateLocal(
        IReadOnlyList<IAuthenticationProvider> authenticationProviders,
        IReadOnlyList<ITenantResolver> tenantResolvers,
        IReadOnlyList<IEntitlementContributor> entitlementContributors)
    {
        if (authenticationProviders.Count > 0)
        {
            return Forbidden("IAuthenticationProvider", authenticationProviders[0].Name);
        }

        if (tenantResolvers.Count > 0)
        {
            return Forbidden("ITenantResolver", tenantResolvers[0].Name);
        }

        // Compared by type, not by name: a contributor registered under the baseline's name is not
        // the baseline, and the registry's duplicate-name rule already guarantees the real one could
        // not be registered beside it.
        var foreign = entitlementContributors
            .FirstOrDefault(contributor => contributor is not CommunityEntitlementContributor);

        return foreign is null
            ? null
            : Forbidden("IEntitlementContributor", foreign.Name.Value);
    }

    private static CompositionViolation Forbidden(string contract, string registration) =>
        new(
            CompositionFinding.RegistrationForbiddenByProfile,
            $"The host declared the '{CompositionProfile.Local}' profile, which is identity-free, "
            + $"single-tenant and licence-free, and registered the {contract} '{registration}'. Remove "
            + $"the registration, or declare the '{CompositionProfile.Operated}' profile.");

    private static string Describe(IEnumerable<string> names) =>
        string.Join(", ", names.Select(name => $"'{name}'"));
}
