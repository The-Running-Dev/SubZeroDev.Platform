using Microsoft.Extensions.Logging;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Persistence;

namespace SubZeroDev.Platform.Identity;

/// <summary>Replaces a linked identity's principal with its account's. Registered only by
/// <see cref="IdentityAccountsModule"/>, so a host without the store keeps its principals unchanged
/// (I-I21).</summary>
/// <remarks>Only the generic path and the test-grade provider are mapped; the upstream proxy and a
/// consumer's own provider establish the principal they mean. The lookup is not cached, so a link or
/// an unlink is seen on the next request. A store failure fails authentication: answering the
/// unlinked pair would let a request act as a principal the person has stopped being (I-I20).</remarks>
internal sealed class AccountPrincipalMapping(
    IUnitOfWork unitOfWork,
    AccountStore store,
    EstablishedPrincipals established,
    ILogger<AccountPrincipalMapping> logger) : IPrincipalMapping
{
    public async Task<Result<Principal, AuthenticationError>> MapAsync(
        IAuthenticationProvider provider,
        Principal principal,
        CancellationToken cancellationToken)
    {
        // No issuer may impersonate the account namespace: the account principal is Identity's to
        // establish, never a provider's.
        if (string.Equals(principal.Id.Issuer, AccountId.PrincipalIssuer, StringComparison.Ordinal))
        {
            logger.LogWarning(
                "Provider {Provider} established a principal in the account namespace; rejected.", provider.Name);
            return Result<Principal, AuthenticationError>.Failure(AuthenticationError.CredentialRejected(provider.Name));
        }

        if (principal.Kind != PrincipalKind.Account || !AccountApi.IsMappedProvider(provider))
        {
            return Result<Principal, AuthenticationError>.Success(principal);
        }

        var linked = await unitOfWork.ExecuteAsync(
            TransactionIntent.ReadOnly,
            token => store.FindLinkedAccountAsync(principal.Id, token),
            cancellationToken).ConfigureAwait(false);

        if (!linked.IsSuccess)
        {
            logger.LogWarning(
                "Account lookup for provider {Provider} failed: {Code}.", provider.Name, linked.Error.Code);
            return Result<Principal, AuthenticationError>.Failure(AuthenticationError.ProviderFailed(provider.Name));
        }

        if (linked.Value is { } account)
        {
            return Result<Principal, AuthenticationError>.Success(principal with { Id = account.ToPrincipalId() });
        }

        established.Add(principal);
        return Result<Principal, AuthenticationError>.Success(principal);
    }
}
