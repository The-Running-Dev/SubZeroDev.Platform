using SubZeroDev.Platform.Abstractions;

namespace SubZeroDev.Platform.Billing;

/// <summary>A plan's stable key. <c>design/20-contract.md</c>, Types §8.</summary>
/// <param name="Value">The key.</param>
public readonly record struct PlanKey(string Value)
{
    /// <summary>The key.</summary>
    public string Value { get; } = Value ?? throw new ArgumentNullException(nameof(Value));

    /// <inheritdoc/>
    public override string ToString() => Value;
}

/// <summary>A subscription's identity. <c>design/20-contract.md</c>, Types §8. Never read outside
/// Billing (I-C8) — product code asks <see cref="IEntitlementEvaluator"/>, not this.</summary>
/// <param name="Value">The identifier.</param>
public readonly record struct SubscriptionId(Guid Value)
{
    /// <summary>Mints a new identifier.</summary>
    /// <returns>The identifier.</returns>
    public static SubscriptionId CreateNew() => new(Guid.NewGuid());

    /// <inheritdoc/>
    public override string ToString() => Value.ToString();
}

/// <summary>A subscription's state. <c>design/20-contract.md</c>, Types §8. Must never be read outside
/// this module (I-C8) — an architecture test enforces it.</summary>
public enum SubscriptionState
{
    /// <summary>In trial. Grants the plan's features (S11.1) on the same terms as
    /// <see cref="Active"/> — a trial that did not grant would not be a trial.</summary>
    Trialing,

    /// <summary>Paid and current. Grants the plan's features.</summary>
    Active,

    /// <summary>Payment failed; access suspended pending resolution. Does not grant.</summary>
    PastDue,

    /// <summary>Cancelled by the tenant or an operator. Terminal — does not grant, and no further
    /// transition is reachable from it.</summary>
    Cancelled,

    /// <summary>Explicitly closed out, distinct from a subscription whose period has simply lapsed
    /// (S11.4 lapses with no state write at all). Terminal, like <see cref="Cancelled"/>.</summary>
    Expired,
}

/// <summary>A plan: a stable key, a display name and the features it grants.
/// <c>design/20-contract.md</c>, Types §8.</summary>
/// <param name="Key">The plan's stable key.</param>
/// <param name="DisplayName">The plan's display name.</param>
/// <param name="Features">The features this plan grants.</param>
public sealed record Plan(
    PlanKey Key,
    string DisplayName,
    IReadOnlySet<FeatureName> Features);

/// <summary>A tenant's subscription. <c>design/20-contract.md</c>, Types §8. Entitlement is derived
/// from <see cref="Plan"/> and <see cref="State"/> against <see cref="IClock"/> — never stored (I-B4).</summary>
/// <param name="Id">The subscription's identity.</param>
/// <param name="Tenant">The subscribed tenant. At most one subscription per tenant (I-O8, S11.8).</param>
/// <param name="Plan">The subscribed plan's key.</param>
/// <param name="State">The subscription's state.</param>
/// <param name="PeriodStart">When the current period started.</param>
/// <param name="PeriodEnd">When the current period ends. Once the clock passes this instant the
/// subscription stops granting with no row written (S11.4).</param>
/// <param name="TrialEndsAt">When a trial ends, if this subscription started as one.</param>
/// <param name="ProviderReference">Opaque; identifies the subscription to whichever provider owns
/// it. Never parsed by Platform.</param>
public sealed record Subscription(
    SubscriptionId Id,
    TenantId Tenant,
    PlanKey Plan,
    SubscriptionState State,
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd,
    DateTimeOffset? TrialEndsAt,
    string ProviderReference);

/// <summary>Records that one inbound provider event was processed, so a redelivery is a no-op
/// (I-B7). <c>design/20-contract.md</c>, Types §8.</summary>
/// <param name="Provider">The provider's name.</param>
/// <param name="ProviderEventId">The provider's own event identifier.</param>
/// <param name="ReceivedAt">When this event was first received.</param>
public sealed record ProviderEventReceipt(
    string Provider,
    string ProviderEventId,
    DateTimeOffset ReceivedAt);

/// <summary>One inbound event from a billing provider. Not a contract-fixed type — the provider seam
/// is asynchronous and D5 integrates no real provider, so this shape is Billing's own, internal to
/// how <see cref="IBillingApi.HandleProviderEventAsync"/> is driven.</summary>
/// <param name="Provider">The provider's name.</param>
/// <param name="ProviderEventId">The provider's own event identifier — the idempotency key
/// alongside <paramref name="Provider"/>.</param>
/// <param name="Tenant">The subscription's tenant.</param>
/// <param name="Plan">The plan the provider reports.</param>
/// <param name="State">The state the provider reports.</param>
/// <param name="PeriodStart">The period start the provider reports.</param>
/// <param name="PeriodEnd">The period end the provider reports.</param>
/// <param name="TrialEndsAt">The trial end the provider reports, if any.</param>
/// <param name="ProviderReference">The provider's own reference for the subscription.</param>
public sealed record ProviderEvent(
    string Provider,
    string ProviderEventId,
    TenantId Tenant,
    PlanKey Plan,
    SubscriptionState State,
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd,
    DateTimeOffset? TrialEndsAt,
    string ProviderReference);

/// <summary>Why a billing operation did not complete. <c>design/20-contract.md</c>, Error semantics
/// §6 — exactly these four variants.</summary>
public sealed record BillingError : PlatformError
{
    private BillingError(string code, bool isRetryable, string detail)
        : base(code)
    {
        IsRetryable = isRetryable;
        Detail = detail;
    }

    /// <summary>Names the cause.</summary>
    public string Detail { get; }

    /// <inheritdoc/>
    public override bool IsRetryable { get; }

    /// <summary>A transition names a plan that is not registered.</summary>
    /// <returns>The error.</returns>
    public static BillingError PlanNotFound() =>
        new(nameof(PlanNotFound), isRetryable: false, "The named plan is not registered.");

    /// <summary>Administration acts on a tenant with no subscription.</summary>
    /// <returns>The error.</returns>
    public static BillingError SubscriptionNotFound() =>
        new(nameof(SubscriptionNotFound), isRetryable: false, "The tenant has no subscription.");

    /// <summary>The target state is unreachable from the current one — including a concurrent
    /// transition that already claimed the unique subscription row (S11.8), which this answers
    /// identically to an ordinary invalid transition rather than inventing a fifth variant for a
    /// race; a caller that wants to retry rereads the state the winner left and decides again from
    /// there.</summary>
    /// <returns>The error.</returns>
    public static BillingError InvalidTransition() =>
        new(nameof(InvalidTransition), isRetryable: false, "The target state is unreachable from the current one.");

    /// <summary>An inbound provider event could not be interpreted. Never raised for a redelivered
    /// event — that is idempotent success (I-B7).</summary>
    /// <returns>The error.</returns>
    public static BillingError ProviderEventMalformed() =>
        new(nameof(ProviderEventMalformed), isRetryable: false, "The inbound provider event could not be interpreted.");
}
