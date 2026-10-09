using Microsoft.Extensions.Logging;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Persistence;

namespace SubZeroDev.Platform.Identity;

/// <inheritdoc cref="IAccountApi"/>
internal sealed class AccountApi(
    IUnitOfWork unitOfWork,
    AccountStore store,
    EstablishedPrincipals established,
    ICurrentPrincipal currentPrincipal,
    IAuditWriter auditWriter,
    IClock clock,
    ILogger<AccountApi> logger) : IAccountApi
{
    public async Task<Result<Account, AccountError>> CreateAccountAsync(CancellationToken cancellationToken)
    {
        var principal = currentPrincipal.Current;
        if (!IsEligible(principal))
        {
            return Result<Account, AccountError>.Failure(AccountError.NotEligible());
        }

        var identity = principal.Id;
        var now = clock.UtcNow;
        var alreadyLinked = false;

        var result = await unitOfWork.ExecuteAsync(
            TransactionIntent.Write,
            async token =>
            {
                var account = AccountId.CreateNew();
                await store.InsertAccountAsync(account, now, token).ConfigureAwait(false);

                try
                {
                    await store.InsertLinkAsync(identity, account, now, token).ConfigureAwait(false);
                }
                catch (Exception exception) when (AccountStore.IsUniqueViolation(exception))
                {
                    // Rethrown deliberately: swallowing it would leave a PostgreSQL transaction
                    // aborted. The flag is read only after ExecuteAsync has rolled back.
                    alreadyLinked = true;
                    throw;
                }

                await auditWriter.WriteAsync(
                    IdentityAuditActions.AccountCreated,
                    new ResourceRef("Account", account.Value),
                    AuditOutcome.Allowed,
                    AuditClass.Required,
                    token).ConfigureAwait(false);

                return new Account(account, now, [new LinkedIdentity(identity, now)]);
            },
            cancellationToken).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            return Result<Account, AccountError>.Success(result.Value);
        }

        if (alreadyLinked)
        {
            logger.LogInformation("Account create found the identity already linked; nothing was written.");
            return Result<Account, AccountError>.Failure(AccountError.IdentityAlreadyLinked());
        }

        logger.LogWarning("Account create failed: {Code}.", result.Error.Code);
        return Result<Account, AccountError>.Failure(AccountError.StoreUnavailable());
    }

    public async Task<Result<Account, AccountError>> GetCurrentAccountAsync(CancellationToken cancellationToken)
    {
        var principal = currentPrincipal.Current;
        if (!IsAccountPrincipal(principal))
        {
            return Result<Account, AccountError>.Failure(AccountError.NotAnAccount());
        }

        var result = await unitOfWork.ExecuteAsync(
            TransactionIntent.ReadOnly,
            token => store.FindAccountAsync(new AccountId(principal.Id.Subject), token),
            cancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            logger.LogWarning("Account read failed: {Code}.", result.Error.Code);
            return Result<Account, AccountError>.Failure(AccountError.StoreUnavailable());
        }

        return result.Value is { } account
            ? Result<Account, AccountError>.Success(account)
            : Result<Account, AccountError>.Failure(AccountError.NotAnAccount());
    }

    /// <summary>Whether a principal is an account principal: kind <see cref="PrincipalKind.Account"/>
    /// and issuer <see cref="AccountId.PrincipalIssuer"/>. Both are needed.</summary>
    internal static bool IsAccountPrincipal(Principal principal) =>
        principal.Kind == PrincipalKind.Account
        && string.Equals(principal.Id.Issuer, AccountId.PrincipalIssuer, StringComparison.Ordinal);

    /// <summary>Whether a provider is one whose principals Identity maps and may make into an account:
    /// the generic path or the test-grade provider. Never the upstream proxy, never a consumer's.</summary>
    internal static bool IsMappedProvider(IAuthenticationProvider provider) =>
        provider is ConfiguredBearerAuthenticationProvider or JwtBearerAuthenticationProvider;

    /// <summary>An unlinked <see cref="PrincipalKind.Account"/> principal an Identity provider
    /// established: the mapping saw it unlinked and passed it through. A linked one already arrives as
    /// its account principal; a pair linked by a concurrent call since is caught by the link's primary
    /// key.</summary>
    private bool IsEligible(Principal principal) =>
        principal.Kind == PrincipalKind.Account
        && !string.Equals(principal.Id.Issuer, AccountId.PrincipalIssuer, StringComparison.Ordinal)
        && established.Contains(principal);
}
