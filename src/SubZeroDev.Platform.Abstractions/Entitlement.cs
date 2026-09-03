namespace SubZeroDev.Platform.Abstractions;

/// <summary>The only entitlement question product code asks. It never asks about a subscription or
/// a licence.</summary>
/// <param name="Value">The stable feature name.</param>
public readonly record struct FeatureName(string Value)
{
    /// <summary>The stable feature name.</summary>
    public string Value { get; } = Value ?? throw new ArgumentNullException(nameof(Value));

    /// <inheritdoc/>
    public override string ToString() => Value;
}

/// <summary>A registered entitlement contributor's name, so a decision can name which one
/// granted.</summary>
/// <param name="Value">The contributor's name.</param>
public readonly record struct EntitlementContributorName(string Value)
{
    /// <summary>The contributor's name.</summary>
    public string Value { get; } = Value ?? throw new ArgumentNullException(nameof(Value));

    /// <inheritdoc/>
    public override string ToString() => Value;
}

/// <summary>The admission decision. A value rather than a call result, because a unit of work carries
/// the decision that admitted it and nothing re-evaluates it while it runs. Platform stores no
/// entitlement — a consumer persisting one owns its own column shape.</summary>
/// <param name="Feature">The feature asked about.</param>
/// <param name="Tenant">The tenant the check was evaluated in.</param>
/// <param name="Granted">Whether any registered contributor granted the feature.</param>
/// <param name="DecidedAt">When the decision was made — read back from a stored work item, this is
/// the decision that admitted it, not a fresh one.</param>
/// <param name="Sources">The contributors that granted. Non-empty if and only if
/// <paramref name="Granted"/> is <see langword="true"/>. A set: its order carries no meaning.</param>
public sealed record EntitlementDecision(
    FeatureName Feature,
    TenantId Tenant,
    bool Granted,
    DateTimeOffset DecidedAt,
    IReadOnlyCollection<EntitlementContributorName> Sources);

/// <summary>Contributes entitlement. Billing and Licensing each register one; a Local host registers
/// only the Community baseline. No caller may reach a contributor directly — the evaluator is the
/// only surface.</summary>
public interface IEntitlementContributor
{
    /// <summary>The contributor's name, unique among registered contributors.</summary>
    EntitlementContributorName Name { get; }

    /// <summary>Answers whether this contributor grants the feature, in the given tenant. An error
    /// contributes nothing and does not fail the evaluation — closed for new grants, open for stored
    /// claims. Must not audit: the evaluator audits nothing here either, because a decision is not
    /// itself an audited fact.</summary>
    /// <param name="feature">The feature being checked.</param>
    /// <param name="tenant">The tenant the check is evaluated in.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>Whether this contributor grants the feature, or why it could not answer.</returns>
    Task<Result<bool, EntitlementError>> GrantsAsync(
        FeatureName feature,
        TenantId tenant,
        CancellationToken cancellationToken);
}

/// <summary>The single entitlement question. Framework packages, modules and product code all ask
/// through it.</summary>
public interface IEntitlementEvaluator
{
    /// <summary>Evaluates one feature against the ambient tenant — not a parameter, so no call site
    /// can evaluate in a tenant the request did not resolve. Always returns a decision; a contributor
    /// that could not answer contributes nothing rather than failing the evaluation.</summary>
    /// <param name="feature">The feature to check.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>The decision.</returns>
    Task<EntitlementDecision> EvaluateAsync(FeatureName feature, CancellationToken cancellationToken);
}

/// <summary>Why an entitlement question could not be answered, or why a caller refused an
/// operation.</summary>
public sealed record EntitlementError : PlatformError
{
    private EntitlementError(string code, bool isRetryable, string detail)
        : base(code)
    {
        IsRetryable = isRetryable;
        Detail = detail;
    }

    /// <summary>Names the cause.</summary>
    public string Detail { get; }

    /// <inheritdoc/>
    public override bool IsRetryable { get; }

    /// <summary>A contributor could not answer — typically an unreachable store. Contributes nothing;
    /// the evaluation continues with whatever the others answered.</summary>
    /// <param name="contributor">The contributor that could not answer.</param>
    /// <returns>The error.</returns>
    public static EntitlementError ContributorUnavailable(EntitlementContributorName contributor) =>
        new(
            nameof(ContributorUnavailable),
            isRetryable: true,
            $"Entitlement contributor '{contributor}' could not answer.");

    /// <summary>Raised by a caller refusing admission on a decision with <c>Granted == false</c>.
    /// Never raised by the evaluator itself, and never names which contributor declined — a
    /// self-hosted deployment's licence state is not an operated caller's business, and the reverse
    /// holds too.</summary>
    /// <param name="feature">The feature that was not entitled.</param>
    /// <returns>The error.</returns>
    public static EntitlementError FeatureNotEntitled(FeatureName feature) =>
        new(nameof(FeatureNotEntitled), isRetryable: false, $"Feature '{feature}' is not entitled.");
}
