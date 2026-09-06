using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Persistence;

namespace SubZeroDev.Platform.Billing;

/// <summary>Billing's <see cref="IEntitlementContributor"/>. Grants a feature when the tenant holds a
/// subscription in a granting state (<see cref="SubscriptionState.Trialing"/> or
/// <see cref="SubscriptionState.Active"/>) whose period has not yet ended against <see cref="IClock"/>,
/// on a plan that grants the feature (S11.1). A subscription whose period has ended stops granting
/// with no row written — the clock comparison alone does that (S11.4, I-B4).</summary>
/// <remarks>Opens its own unit of work rather than assuming one is ambient: <see cref="IEntitlementEvaluator"/>
/// calls every registered contributor directly, with no transaction open around the call — the same
/// reason <c>OrganizationsPermissionProvider</c> does the same.</remarks>
internal sealed class BillingEntitlementContributor(
    IUnitOfWork unitOfWork, BillingStore store, IClock clock) : IEntitlementContributor
{
    public EntitlementContributorName Name { get; } = new("Platform.Billing");

    public async Task<Result<bool, EntitlementError>> GrantsAsync(
        FeatureName feature, TenantId tenant, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        var result = await unitOfWork.ExecuteAsync(
            TransactionIntent.ReadOnly,
            async ct =>
            {
                var subscription = await store.FindSubscriptionAsync(tenant, ct).ConfigureAwait(false);
                if (subscription is not { } value || !IsGranting(value.State) || now >= value.PeriodEnd)
                {
                    return false;
                }

                return await store.PlanGrantsFeatureAsync(value.Plan, feature, ct).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);

        return result.IsSuccess
            ? Result<bool, EntitlementError>.Success(result.Value)
            : Result<bool, EntitlementError>.Failure(EntitlementError.ContributorUnavailable(Name));
    }

    private static bool IsGranting(SubscriptionState state) =>
        state is SubscriptionState.Trialing or SubscriptionState.Active;
}
