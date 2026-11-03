using SubZeroDev.Platform.Abstractions;

namespace SubZeroDev.Platform.Core;

/// <summary>Which composition rule a host's registrations broke. Core decides; Hosting names the
/// failure, because <c>HostStartupError</c> is Hosting's and Core may not reference it.</summary>
internal enum CompositionFinding
{
    /// <summary><see cref="CompositionProfile.Operated"/> or
    /// <see cref="CompositionProfile.AuthenticatedTransport"/> with no authentication provider
    /// registered — I-C1, I-C14.</summary>
    AuthenticationProviderRequired,

    /// <summary><see cref="CompositionProfile.Operated"/> with no sink declaring
    /// <see cref="IAuditSink.IsDurable"/> — I-C2.</summary>
    DurableAuditSinkRequired,

    /// <summary><see cref="CompositionProfile.Local"/> or
    /// <see cref="CompositionProfile.AuthenticatedTransport"/> with a registration the profile
    /// forbids — I-C3, I-C15.</summary>
    RegistrationForbiddenByProfile,
}

/// <summary>Marks a module an <see cref="CompositionProfile.AuthenticatedTransport"/> host may
/// compose. Internal, so only a Platform package can claim it, and only for a module that keeps no
/// state, writes no <see cref="AuditClass.Required"/> record and grants no permission: the profile
/// admits what is marked and refuses everything else, so a module added later is refused until it is
/// examined rather than admitted until somebody notices (I-C15).</summary>
internal interface ITransportCompatibleModule
{
}

/// <summary>Registered by every package that gives the host durable Platform state — today,
/// Persistence. An <see cref="CompositionProfile.AuthenticatedTransport"/> host holds nothing about
/// its callers, so the presence of one fails startup (I-C15).</summary>
/// <param name="Registrant">The package that registered it, named in the startup failure.</param>
internal sealed record DurableStateRegistration(string Registrant);

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
    /// <param name="permissionProviders">The frozen permission providers.</param>
    /// <param name="modules">The composed modules, in resolved order.</param>
    /// <param name="durableState">The durable-state registrations.</param>
    /// <returns>The first violation, or <see langword="null"/> when the composition holds.</returns>
    internal static CompositionViolation? Validate(
        CompositionProfile profile,
        IReadOnlyList<IAuthenticationProvider> authenticationProviders,
        IReadOnlyList<IAuditSink> auditSinks,
        IReadOnlyList<ITenantResolver> tenantResolvers,
        IReadOnlyList<IEntitlementContributor> entitlementContributors,
        IReadOnlyList<IPermissionProvider> permissionProviders,
        IReadOnlyList<IPlatformModule> modules,
        IReadOnlyList<DurableStateRegistration> durableState)
    {
        ArgumentNullException.ThrowIfNull(authenticationProviders);
        ArgumentNullException.ThrowIfNull(auditSinks);
        ArgumentNullException.ThrowIfNull(tenantResolvers);
        ArgumentNullException.ThrowIfNull(entitlementContributors);
        ArgumentNullException.ThrowIfNull(permissionProviders);
        ArgumentNullException.ThrowIfNull(modules);
        ArgumentNullException.ThrowIfNull(durableState);

        // No default that passes: a profile this switch does not name is a host whose composition
        // nothing checks, which is the failure the validator exists to prevent.
        return profile switch
        {
            CompositionProfile.Operated => ValidateOperated(authenticationProviders, auditSinks),
            CompositionProfile.Local => ValidateLocal(
                authenticationProviders, tenantResolvers, entitlementContributors),
            CompositionProfile.AuthenticatedTransport => ValidateAuthenticatedTransport(
                authenticationProviders, auditSinks, tenantResolvers, entitlementContributors,
                permissionProviders, modules, durableState),
            _ => throw new ArgumentOutOfRangeException(
                nameof(profile), profile, "The composition profile is not one the validator knows."),
        };
    }

    /// <summary>The part of I-C15 the host checks while it composes, before any module's
    /// <see cref="IPlatformModule.Register"/> runs and before any hosted service is built: an
    /// <see cref="CompositionProfile.AuthenticatedTransport"/> host admits only modules marked
    /// <see cref="ITransportCompatibleModule"/>, and no durable Platform state. Checked this early so
    /// a refused module or package is named as itself — not by whichever of its services first fails
    /// to activate or connect — and does no work first. <see cref="Validate"/> repeats both, for a
    /// registration made after the host composed.</summary>
    /// <param name="profile">The profile the host declared.</param>
    /// <param name="modules">The registered modules.</param>
    /// <param name="durableState">The durable-state registrations made so far.</param>
    /// <returns>The first violation, or <see langword="null"/>.</returns>
    internal static CompositionViolation? ValidateBeforeComposition(
        CompositionProfile profile,
        IReadOnlyList<IPlatformModule> modules,
        IReadOnlyList<DurableStateRegistration> durableState)
    {
        ArgumentNullException.ThrowIfNull(modules);
        ArgumentNullException.ThrowIfNull(durableState);

        if (profile != CompositionProfile.AuthenticatedTransport)
        {
            return null;
        }

        var module = modules.FirstOrDefault(candidate => candidate is not ITransportCompatibleModule);
        if (module is not null)
        {
            return ForbiddenOnTransport("module", module.Name.Value);
        }

        return durableState.Count > 0
            ? ForbiddenOnTransport("durable Platform state of", durableState[0].Registrant)
            : null;
    }

    /// <summary>The endpoint half of I-C15, checked once the endpoint table is final: an
    /// <see cref="CompositionProfile.AuthenticatedTransport"/> host grants no permission, so an
    /// endpoint that requires one could never be reached, and is refused at startup rather than
    /// denied on every call.</summary>
    /// <param name="profile">The profile the host declared.</param>
    /// <param name="route">The endpoint's route, named in the finding.</param>
    /// <param name="requiredPermission">The permission the endpoint requires, or
    /// <see langword="null"/> when it carries an exemption instead.</param>
    /// <returns>The violation, or <see langword="null"/> when the endpoint is admissible.</returns>
    internal static CompositionViolation? ValidateEndpoint(
        CompositionProfile profile, string route, string? requiredPermission) =>
        profile == CompositionProfile.AuthenticatedTransport && requiredPermission is not null
            ? ForbiddenOnTransport($"endpoint '{route}', which requires the permission", requiredPermission)
            : null;

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

    private static CompositionViolation? ValidateAuthenticatedTransport(
        IReadOnlyList<IAuthenticationProvider> authenticationProviders,
        IReadOnlyList<IAuditSink> auditSinks,
        IReadOnlyList<ITenantResolver> tenantResolvers,
        IReadOnlyList<IEntitlementContributor> entitlementContributors,
        IReadOnlyList<IPermissionProvider> permissionProviders,
        IReadOnlyList<IPlatformModule> modules,
        IReadOnlyList<DurableStateRegistration> durableState)
    {
        if (authenticationProviders.Count == 0)
        {
            return new CompositionViolation(
                CompositionFinding.AuthenticationProviderRequired,
                $"The host declared the '{CompositionProfile.AuthenticatedTransport}' profile, which "
                + "authenticates its callers, and registered no IAuthenticationProvider. Register one, or "
                + $"declare the '{CompositionProfile.Local}' profile.");
        }

        // Modules first: a refused module accounts for every registration it brought with it, so
        // naming the module is the sentence an operator can act on.
        if (ValidateBeforeComposition(CompositionProfile.AuthenticatedTransport, modules, durableState) is { } early)
        {
            return early;
        }

        var durableSink = auditSinks.FirstOrDefault(sink => sink.IsDurable);
        if (durableSink is not null)
        {
            return ForbiddenOnTransport("durable IAuditSink", durableSink.Name);
        }

        if (tenantResolvers.Count > 0)
        {
            return ForbiddenOnTransport("ITenantResolver", tenantResolvers[0].Name);
        }

        // Both compared by type, as Local's contributor rule is: a registration carrying a
        // baseline's name is not the baseline. The composition provider grants only to Local's
        // operator, so on this profile it grants nothing and every Platform permission is denied.
        var contributor = entitlementContributors
            .FirstOrDefault(candidate => candidate is not CommunityEntitlementContributor);
        if (contributor is not null)
        {
            return ForbiddenOnTransport("IEntitlementContributor", contributor.Name.Value);
        }

        var provider = permissionProviders
            .FirstOrDefault(candidate => candidate is not CompositionPermissionProvider);
        return provider is null
            ? null
            : ForbiddenOnTransport("IPermissionProvider", provider.Name.Value);
    }

    private static CompositionViolation ForbiddenOnTransport(string contract, string registration) =>
        new(
            CompositionFinding.RegistrationForbiddenByProfile,
            $"The host declared the '{CompositionProfile.AuthenticatedTransport}' profile, which "
            + "authenticates its callers and holds nothing about them, and registered the "
            + $"{contract} '{registration}'. Remove the registration, or declare the "
            + $"'{CompositionProfile.Operated}' profile.");

    private static CompositionViolation Forbidden(string contract, string registration) =>
        new(
            CompositionFinding.RegistrationForbiddenByProfile,
            $"The host declared the '{CompositionProfile.Local}' profile, which is identity-free, "
            + $"single-tenant and licence-free, and registered the {contract} '{registration}'. Remove "
            + $"the registration, or declare the '{CompositionProfile.Operated}' profile.");

    private static string Describe(IEnumerable<string> names) =>
        string.Join(", ", names.Select(name => $"'{name}'"));
}
