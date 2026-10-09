using SubZeroDev.Platform.Abstractions;

namespace SubZeroDev.Platform.Identity;

/// <summary>One of Identity's own <see cref="PrincipalKind.Account"/>-producing providers: the generic
/// path and the test-grade provider. Their principals are the ones the account store maps, and their
/// tokens are the ones <see cref="IAccountApi.LinkAsync"/> accepts as a second credential. Never the
/// upstream proxy, never a consumer's provider.</summary>
internal interface ISecondCredentialProvider
{
    /// <summary>Validates <paramref name="token"/> exactly as authentication would, with no mapping,
    /// and then requires it to have been issued recently (<see cref="SecondCredential.Check"/>).</summary>
    /// <param name="token">The compact bearer token.</param>
    /// <param name="now">The time of the call.</param>
    /// <param name="cancellationToken">Cancels the validation.</param>
    /// <returns>The pair the token establishes, or why it is refused.</returns>
    Task<SecondCredentialValidation> ValidateSecondCredentialAsync(
        string token, DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>A second credential's validation: the principal it establishes, or why it was refused.
/// The cause is for the log only; the caller answers one error for all of them.</summary>
/// <param name="Principal">The principal, when accepted.</param>
/// <param name="Claimed">Whether the provider claimed the token's issuer. An unclaimed token is
/// another provider's to judge.</param>
/// <param name="Cause">Why it was refused, when it was.</param>
internal readonly record struct SecondCredentialValidation(Principal? Principal, bool Claimed, string Cause);

/// <summary>The issued-at rule for a second credential (<c>design/20-contract.md</c>, Public surface
/// §19, I-I18): the token proves the person completed that sign-in moments ago, not that they once
/// held a token.</summary>
internal static class SecondCredential
{
    /// <summary>How long before the call a second credential may have been issued. Fixed; not a
    /// setting.</summary>
    internal static readonly TimeSpan MaximumAge = TimeSpan.FromMinutes(5);

    /// <summary>Applies the rule to a provider's own validation. A token with no <c>iat</c> is
    /// refused. The provider's clock tolerance widens the window on either side.</summary>
    /// <param name="validated">The provider's answer.</param>
    /// <param name="issuedAt">The token's <c>iat</c>, when it carried one.</param>
    /// <param name="now">The time of the call.</param>
    /// <param name="clockTolerance">The provider's clock tolerance.</param>
    /// <returns>The validation.</returns>
    internal static SecondCredentialValidation Check(
        Result<Principal, AuthenticationError> validated,
        DateTimeOffset? issuedAt,
        DateTimeOffset now,
        TimeSpan clockTolerance)
    {
        if (!validated.IsSuccess)
        {
            var claimed = validated.Error.Code != nameof(AuthenticationError.CredentialNotClaimed);
            return new SecondCredentialValidation(null, claimed, validated.Error.Code);
        }

        // Identity's to establish, never a provider's: the mapping rejects it too.
        if (string.Equals(validated.Value.Id.Issuer, AccountId.PrincipalIssuer, StringComparison.Ordinal))
        {
            return new SecondCredentialValidation(null, Claimed: true, "AccountNamespace");
        }

        if (issuedAt is not { } issued)
        {
            return new SecondCredentialValidation(null, Claimed: true, "NoIssuedAt");
        }

        if (issued < now - MaximumAge - clockTolerance || issued > now + clockTolerance)
        {
            return new SecondCredentialValidation(null, Claimed: true, "Stale");
        }

        return new SecondCredentialValidation(validated.Value, Claimed: true, "");
    }
}
