using Microsoft.Extensions.Logging;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Persistence;

namespace SubZeroDev.Platform.Billing;

/// <summary>The billing administration API and inbound provider event seam —
/// <c>design/20-contract.md</c>, Modules, "Billing" (Touches, S11).</summary>
public interface IBillingApi
{
    /// <summary>Registers or replaces a plan's display name and feature set. Idempotent: no error
    /// variant models "already registered" because re-registering is ordinary configuration, not a
    /// caller defect.</summary>
    Task<Result<Plan, BillingError>> RegisterPlanAsync(
        PlanKey key, string displayName, IReadOnlySet<FeatureName> features, CancellationToken cancellationToken);

    /// <summary>Reads one tenant's subscription.</summary>
    Task<Result<Subscription, BillingError>> GetSubscriptionAsync(TenantId tenant, CancellationToken cancellationToken);

    /// <summary>Creates a tenant's first subscription, or moves its existing one to a new plan and/or
    /// state. Naming an unregistered plan answers <see cref="BillingError.PlanNotFound"/>; naming a
    /// state unreachable from the current one (or losing a concurrent race for the same tenant's
    /// unique row) answers <see cref="BillingError.InvalidTransition"/> (S11.7). A successful
    /// transition writes exactly one subscription row and one
    /// <see cref="PlatformAuditActions.EntitlementChanged"/> audit record of class
    /// <see cref="AuditClass.Required"/>, in the same transaction (S11.2, S11.10).</summary>
    Task<Result<Subscription, BillingError>> TransitionAsync(
        TenantId tenant,
        PlanKey plan,
        SubscriptionState state,
        DateTimeOffset periodStart,
        DateTimeOffset periodEnd,
        DateTimeOffset? trialEndsAt,
        string providerReference,
        CancellationToken cancellationToken);

    /// <summary>Applies one inbound provider event. A redelivered event (same
    /// <see cref="ProviderEvent.Provider"/> and <see cref="ProviderEvent.ProviderEventId"/>) updates
    /// the subscription at most once, answers success both times, and records exactly one receipt
    /// (S11.5, I-B7). An uninterpretable event answers
    /// <see cref="BillingError.ProviderEventMalformed"/> and records no receipt (S11.6).</summary>
    Task<Result<ProviderEventReceipt, BillingError>> HandleProviderEventAsync(
        ProviderEvent providerEvent, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IBillingApi"/>
internal sealed class BillingApi(
    IUnitOfWork unitOfWork,
    BillingStore store,
    IAuditWriter auditWriter,
    IClock clock,
    ILogger<BillingApi> logger) : IBillingApi
{
    public async Task<Result<Plan, BillingError>> RegisterPlanAsync(
        PlanKey key, string displayName, IReadOnlySet<FeatureName> features, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(features);

        var plan = new Plan(key, displayName, features);
        var result = await unitOfWork.ExecuteAsync(
            TransactionIntent.Write,
            async ct =>
            {
                await store.UpsertPlanAsync(plan, ct).ConfigureAwait(false);
                return Result<Plan, BillingError>.Success(plan);
            },
            cancellationToken).ConfigureAwait(false);

        // No variant models a bare infrastructure failure (design/20-contract.md fixes exactly
        // four); PlanNotFound is the closest of the four to "the write did not take" for an
        // operation that is entirely about plans.
        return Unwrap(result, BillingError.PlanNotFound);
    }

    public async Task<Result<Subscription, BillingError>> GetSubscriptionAsync(
        TenantId tenant, CancellationToken cancellationToken)
    {
        var result = await unitOfWork.ExecuteAsync(
            TransactionIntent.ReadOnly,
            async ct =>
            {
                var subscription = await store.FindSubscriptionAsync(tenant, ct).ConfigureAwait(false);
                return subscription is { } value
                    ? Result<Subscription, BillingError>.Success(value)
                    : Result<Subscription, BillingError>.Failure(BillingError.SubscriptionNotFound());
            },
            cancellationToken).ConfigureAwait(false);

        return Unwrap(result, BillingError.SubscriptionNotFound);
    }

    public async Task<Result<Subscription, BillingError>> TransitionAsync(
        TenantId tenant,
        PlanKey plan,
        SubscriptionState state,
        DateTimeOffset periodStart,
        DateTimeOffset periodEnd,
        DateTimeOffset? trialEndsAt,
        string providerReference,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerReference);

        var raceLost = false;

        var result = await unitOfWork.ExecuteAsync(
            TransactionIntent.Write,
            async ct =>
            {
                var registered = await store.FindPlanAsync(plan, ct).ConfigureAwait(false);
                if (registered is null)
                {
                    return Result<Subscription, BillingError>.Failure(BillingError.PlanNotFound());
                }

                var existing = await store.FindSubscriptionAsync(tenant, ct).ConfigureAwait(false);
                if (!IsValidTransition(existing?.State, state))
                {
                    return Result<Subscription, BillingError>.Failure(BillingError.InvalidTransition());
                }

                var subscription = new Subscription(
                    existing?.Id ?? SubscriptionId.CreateNew(),
                    tenant,
                    plan,
                    state,
                    periodStart,
                    periodEnd,
                    trialEndsAt,
                    providerReference);

                try
                {
                    if (existing is null)
                    {
                        await store.InsertSubscriptionAsync(subscription, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        await store.UpdateSubscriptionAsync(subscription, ct).ConfigureAwait(false);
                    }
                }
                catch (Exception exception) when (BillingStore.IsUniqueViolation(exception))
                {
                    // Rethrown deliberately (see BillingStore.IsUniqueViolation's remarks): the flag
                    // is inspected only after ExecuteAsync has rolled the transaction back through
                    // its own normal path.
                    raceLost = true;
                    throw;
                }

                await auditWriter.WriteAsync(
                    PlatformAuditActions.EntitlementChanged,
                    new ResourceRef("Subscription", subscription.Id.ToString()),
                    AuditOutcome.Allowed,
                    AuditClass.Required,
                    ct).ConfigureAwait(false);

                return Result<Subscription, BillingError>.Success(subscription);
            },
            cancellationToken).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            return result.Value;
        }

        if (raceLost)
        {
            logger.LogInformation("Subscription transition lost a race for tenant '{Tenant}'.", tenant);
            return Result<Subscription, BillingError>.Failure(BillingError.InvalidTransition());
        }

        logger.LogWarning("Subscription transition failed: {Code}.", result.Error.Code);
        return Result<Subscription, BillingError>.Failure(BillingError.InvalidTransition());
    }

    public async Task<Result<ProviderEventReceipt, BillingError>> HandleProviderEventAsync(
        ProviderEvent providerEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(providerEvent);

        if (!IsWellFormed(providerEvent))
        {
            return Result<ProviderEventReceipt, BillingError>.Failure(BillingError.ProviderEventMalformed());
        }

        var receipt = new ProviderEventReceipt(
            providerEvent.Provider, providerEvent.ProviderEventId, clock.UtcNow);
        var duplicate = false;

        var result = await unitOfWork.ExecuteAsync(
            TransactionIntent.Write,
            async ct =>
            {
                try
                {
                    await store.InsertReceiptAsync(receipt, ct).ConfigureAwait(false);
                }
                catch (Exception exception) when (BillingStore.IsUniqueViolation(exception))
                {
                    // Rethrown deliberately, same reasoning as TransitionAsync above: this event was
                    // already recorded, and nothing else in this transaction may run on PostgreSQL
                    // once a statement has faulted it.
                    duplicate = true;
                    throw;
                }

                var subscription = new Subscription(
                    SubscriptionId.CreateNew(),
                    providerEvent.Tenant,
                    providerEvent.Plan,
                    providerEvent.State,
                    providerEvent.PeriodStart,
                    providerEvent.PeriodEnd,
                    providerEvent.TrialEndsAt,
                    providerEvent.ProviderReference);

                await store.UpsertSubscriptionFromProviderAsync(subscription, ct).ConfigureAwait(false);

                await auditWriter.WriteAsync(
                    PlatformAuditActions.EntitlementChanged,
                    new ResourceRef("Subscription", subscription.Tenant.ToString()),
                    AuditOutcome.Allowed,
                    AuditClass.Required,
                    ct).ConfigureAwait(false);

                return receipt;
            },
            cancellationToken).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            return Result<ProviderEventReceipt, BillingError>.Success(result.Value);
        }

        if (duplicate)
        {
            // I-B7: a redelivered event is idempotent success, never an error — an ordinary retry
            // must never look like a fault to the provider.
            logger.LogInformation(
                "Provider event '{Provider}'/'{EventId}' already recorded; treating as idempotent success.",
                providerEvent.Provider,
                providerEvent.ProviderEventId);
            return Result<ProviderEventReceipt, BillingError>.Success(receipt);
        }

        logger.LogWarning("Provider event handling failed: {Code}.", result.Error.Code);
        return Result<ProviderEventReceipt, BillingError>.Failure(BillingError.ProviderEventMalformed());
    }

    /// <summary>The subscription state machine. <see langword="null"/> represents "no subscription
    /// yet". A same-state target is always reachable — that is a plan change alone (S11.2).
    /// <see cref="SubscriptionState.Cancelled"/> and <see cref="SubscriptionState.Expired"/> are
    /// terminal: nothing is reachable from either (S11.7).</summary>
    private static bool IsValidTransition(SubscriptionState? current, SubscriptionState target)
    {
        if (current == target)
        {
            return true;
        }

        return current switch
        {
            null => target is SubscriptionState.Trialing or SubscriptionState.Active,
            SubscriptionState.Trialing => target is SubscriptionState.Active or SubscriptionState.Cancelled,
            SubscriptionState.Active =>
                target is SubscriptionState.PastDue or SubscriptionState.Cancelled or SubscriptionState.Expired,
            SubscriptionState.PastDue => target is SubscriptionState.Active or SubscriptionState.Cancelled,
            SubscriptionState.Cancelled => false,
            SubscriptionState.Expired => false,
            _ => false,
        };
    }

    private static bool IsWellFormed(ProviderEvent providerEvent) =>
        !string.IsNullOrWhiteSpace(providerEvent.Provider)
        && !string.IsNullOrWhiteSpace(providerEvent.ProviderEventId)
        && !string.IsNullOrWhiteSpace(providerEvent.ProviderReference)
        && providerEvent.PeriodEnd > providerEvent.PeriodStart;

    private static Result<T, BillingError> Unwrap<T>(
        Result<Result<T, BillingError>, TransactionError> outer, Func<BillingError> fallback) =>
        outer.IsSuccess
            ? outer.Value
            : Result<T, BillingError>.Failure(fallback());
}
