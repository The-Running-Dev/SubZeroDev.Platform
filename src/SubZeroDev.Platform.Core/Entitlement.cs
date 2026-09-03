using SubZeroDev.Platform.Abstractions;

namespace SubZeroDev.Platform.Core;

/// <summary>A rejected entitlement contributor registration.</summary>
public sealed record EntitlementContributorRegistrationError : PlatformError
{
    private EntitlementContributorRegistrationError(string code, string detail)
        : base(code) => Detail = detail;

    /// <summary>Names the contributors involved.</summary>
    public string Detail { get; }

    /// <inheritdoc/>
    public override bool IsRetryable => false;

    /// <summary>Two contributors share a name — a decision naming its source would be worthless if
    /// two sources shared one.</summary>
    /// <param name="name">The duplicated name.</param>
    /// <param name="first">The contributor already registered under that name.</param>
    /// <param name="second">The contributor that tried to register alongside it.</param>
    /// <returns>The error.</returns>
    public static EntitlementContributorRegistrationError DuplicateContributorName(
        EntitlementContributorName name, string first, string second) =>
        new(
            nameof(DuplicateContributorName),
            $"Two entitlement contributors are registered under the name '{name}': '{first}' and '{second}'.");

    /// <summary>Registration was attempted after the registry was frozen.</summary>
    /// <param name="name">The contributor that arrived late.</param>
    /// <returns>The error.</returns>
    public static EntitlementContributorRegistrationError RegistryFrozen(EntitlementContributorName name) =>
        new(
            nameof(RegistryFrozen),
            $"The entitlement contributor registry is frozen; '{name}' cannot be registered.");
}

/// <summary>Collects entitlement contributor registrations. Not resolvable from the ambient
/// container by ordinary means — <see cref="IEntitlementEvaluator"/> is the only public
/// surface a caller reaches a contributor's answer through.</summary>
public interface IEntitlementContributorRegistry
{
    /// <summary>Registers one contributor.</summary>
    /// <param name="contributor">The contributor.</param>
    /// <returns>Success, or why the registration was rejected.</returns>
    Result<EntitlementContributorRegistrationError> Register(IEntitlementContributor contributor);

    /// <summary>Everything registered, in registration order.</summary>
    IReadOnlyList<IEntitlementContributor> Registered { get; }

    /// <summary>Closes the registry. One-way: registration after this is a defect, not a
    /// condition.</summary>
    void Freeze();
}

/// <inheritdoc cref="IEntitlementContributorRegistry"/>
internal sealed class EntitlementContributorRegistry : IEntitlementContributorRegistry
{
    private readonly List<IEntitlementContributor> _registered = [];
    private readonly Lock _gate = new();
    private bool _frozen;

    /// <inheritdoc/>
    public IReadOnlyList<IEntitlementContributor> Registered => _registered;

    /// <inheritdoc/>
    public Result<EntitlementContributorRegistrationError> Register(IEntitlementContributor contributor)
    {
        ArgumentNullException.ThrowIfNull(contributor);

        lock (_gate)
        {
            if (_frozen)
            {
                return Result<EntitlementContributorRegistrationError>.Failure(
                    EntitlementContributorRegistrationError.RegistryFrozen(contributor.Name));
            }

            var existing = _registered.FirstOrDefault(registered => registered.Name == contributor.Name);
            if (existing is not null)
            {
                return Result<EntitlementContributorRegistrationError>.Failure(
                    EntitlementContributorRegistrationError.DuplicateContributorName(
                        contributor.Name, existing.GetType().Name, contributor.GetType().Name));
            }

            _registered.Add(contributor);
            return Result<EntitlementContributorRegistrationError>.Success();
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

/// <summary>The keyed-service key every entitlement contributor registers under. Internal, and
/// deliberately not the plain <see cref="IEntitlementContributor"/> service type: nothing may resolve
/// a contributor by ordinary unkeyed resolution (S7.7) — <see cref="IEntitlementEvaluator"/> stays the
/// only public entry. Billing's and Licensing's registration channel (S11, S12) is not decided by
/// this slice; only the framework's own Community baseline and this repository's tests use this key
/// today.</summary>
internal static class EntitlementContributorRegistration
{
    /// <summary>The key. A <see langword="const string"/> so <c>[FromKeyedServices]</c> can name it.</summary>
    internal const string ServiceKey = "platform.entitlement.contributor";
}

/// <summary>The feature names the Community baseline grants. Empty by default; a consuming host
/// registers its own before composing the platform host to widen it. Internal: D5 has no product
/// naming its own features yet, and Billing's and Licensing's contributors (S11, S12) will need their
/// own registration channel regardless of how this one is shaped.</summary>
/// <param name="Features">The feature names granted with no other contributor registered.</param>
internal sealed record CommunityBaselineOptions(IReadOnlySet<FeatureName> Features)
{
    /// <summary>No features named — the default until a host widens it.</summary>
    internal static CommunityBaselineOptions Empty { get; } = new(new HashSet<FeatureName>());
}

/// <summary>D5's well-known entitlement contributor: the one every host registers, and the only one
/// a <see cref="CompositionProfile.Local"/> host may register (I-C3, S8). Grants exactly the feature
/// names it is configured with — neither answer is an error, because a feature the baseline does not
/// name is simply not granted by it.</summary>
internal sealed class CommunityEntitlementContributor(CommunityBaselineOptions options) : IEntitlementContributor
{
    public EntitlementContributorName Name { get; } = new("Platform.Entitlement.CommunityBaseline");

    public Task<Result<bool, EntitlementError>> GrantsAsync(
        FeatureName feature, TenantId tenant, CancellationToken cancellationToken) =>
        Task.FromResult(Result<bool, EntitlementError>.Success(options.Features.Contains(feature)));
}

/// <inheritdoc cref="IEntitlementEvaluator"/>
/// <remarks>Asks every registered contributor and takes the union. A contributor that errors
/// contributes nothing and does not fail the evaluation — the union proceeds with whatever the
/// others answered. Writes no audit record: an entitlement decision is not itself an audited fact,
/// unlike an authorization denial.</remarks>
internal sealed class EntitlementEvaluator(
    IEntitlementContributorRegistry contributors,
    ICurrentTenant tenant,
    IClock clock) : IEntitlementEvaluator
{
    public async Task<EntitlementDecision> EvaluateAsync(FeatureName feature, CancellationToken cancellationToken)
    {
        var currentTenant = tenant.Current;
        var sources = new List<EntitlementContributorName>();

        foreach (var contributor in contributors.Registered)
        {
            var granted = await contributor
                .GrantsAsync(feature, currentTenant, cancellationToken)
                .ConfigureAwait(false);

            if (granted.IsSuccess && granted.Value)
            {
                sources.Add(contributor.Name);
            }
        }

        return new EntitlementDecision(feature, currentTenant, sources.Count > 0, clock.UtcNow, sources);
    }
}
