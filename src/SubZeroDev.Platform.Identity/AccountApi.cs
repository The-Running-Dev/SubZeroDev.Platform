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
    IAuthenticationProviderRegistry providers,
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

    public async Task<Result<Account, AccountError>> LinkAsync(
        string secondCredential, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(secondCredential);

        var principal = currentPrincipal.Current;
        if (!IsAccountPrincipal(principal))
        {
            return Result<Account, AccountError>.Failure(AccountError.NotAnAccount());
        }

        var now = clock.UtcNow;
        if (await ValidateSecondCredentialAsync(secondCredential, now, cancellationToken).ConfigureAwait(false)
            is not { } identity)
        {
            return Result<Account, AccountError>.Failure(AccountError.SecondCredentialRejected());
        }

        var account = new AccountId(principal.Id.Subject);
        var heldElsewhere = false;
        var alreadyLinked = false;

        var result = await unitOfWork.ExecuteAsync(
            TransactionIntent.Write,
            async token =>
            {
                if (await store.FindAccountAsync(account, token).ConfigureAwait(false) is null)
                {
                    return null;
                }

                var holder = await store.FindLinkedAccountAsync(identity, token).ConfigureAwait(false);
                if (holder is { } held && held != account)
                {
                    heldElsewhere = true;
                    return null;
                }

                if (holder is null)
                {
                    try
                    {
                        await store.InsertLinkAsync(identity, account, now, token).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (AccountStore.IsUniqueViolation(exception))
                    {
                        // A concurrent call linked the pair first. Rethrown so the transaction rolls
                        // back; which account won is read afterwards.
                        alreadyLinked = true;
                        throw;
                    }

                    await auditWriter.WriteAsync(
                        IdentityAuditActions.IdentityLinked,
                        new ResourceRef("Account", account.Value),
                        AuditOutcome.Allowed,
                        AuditClass.Required,
                        token).ConfigureAwait(false);
                }

                return await store.FindAccountAsync(account, token).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);

        if (alreadyLinked)
        {
            result = await unitOfWork.ExecuteAsync(
                TransactionIntent.ReadOnly,
                async token => await store.FindLinkedAccountAsync(identity, token).ConfigureAwait(false) == account
                    ? await store.FindAccountAsync(account, token).ConfigureAwait(false)
                    : null,
                cancellationToken).ConfigureAwait(false);
            heldElsewhere = result.IsSuccess && result.Value is null;
        }

        if (!result.IsSuccess)
        {
            logger.LogWarning("Account link failed: {Code}.", result.Error.Code);
            return Result<Account, AccountError>.Failure(AccountError.StoreUnavailable());
        }

        if (heldElsewhere)
        {
            logger.LogInformation("Account link found the identity linked to another account; nothing was written.");
            return Result<Account, AccountError>.Failure(AccountError.IdentityAlreadyLinked());
        }

        return result.Value is { } linked
            ? Result<Account, AccountError>.Success(linked)
            : Result<Account, AccountError>.Failure(AccountError.NotAnAccount());
    }

    public async Task<Result<Account, AccountError>> UnlinkAsync(
        PrincipalId identity, CancellationToken cancellationToken)
    {
        var principal = currentPrincipal.Current;
        if (!IsAccountPrincipal(principal))
        {
            return Result<Account, AccountError>.Failure(AccountError.NotAnAccount());
        }

        var account = new AccountId(principal.Id.Subject);
        AccountError? refused = null;

        var result = await unitOfWork.ExecuteAsync(
            TransactionIntent.Write,
            async token =>
            {
                if (!await store.LockAccountAsync(account, token).ConfigureAwait(false)
                    || await store.FindAccountAsync(account, token).ConfigureAwait(false) is not { } held)
                {
                    refused = AccountError.NotAnAccount();
                    return null;
                }

                if (!held.Identities.Any(linked => linked.Identity == identity))
                {
                    refused = AccountError.IdentityNotLinked();
                    return null;
                }

                if (held.Identities.Count == 1)
                {
                    refused = AccountError.LastIdentity();
                    return null;
                }

                await store.DeleteLinkAsync(identity, account, token).ConfigureAwait(false);
                await auditWriter.WriteAsync(
                    IdentityAuditActions.IdentityUnlinked,
                    new ResourceRef("Account", account.Value),
                    AuditOutcome.Allowed,
                    AuditClass.Required,
                    token).ConfigureAwait(false);

                return await store.FindAccountAsync(account, token).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            logger.LogWarning("Account unlink failed: {Code}.", result.Error.Code);
            return Result<Account, AccountError>.Failure(AccountError.StoreUnavailable());
        }

        return result.Value is { } remaining
            ? Result<Account, AccountError>.Success(remaining)
            : Result<Account, AccountError>.Failure(refused ?? AccountError.NotAnAccount());
    }

    /// <summary>Whether a principal is an account principal: kind <see cref="PrincipalKind.Account"/>
    /// and issuer <see cref="AccountId.PrincipalIssuer"/>. Both are needed.</summary>
    internal static bool IsAccountPrincipal(Principal principal) =>
        principal.Kind == PrincipalKind.Account
        && string.Equals(principal.Id.Issuer, AccountId.PrincipalIssuer, StringComparison.Ordinal);

    /// <summary>Whether a provider is one whose principals Identity maps and may make into an account,
    /// and whose tokens it accepts as a second credential: the generic path or the test-grade provider
    /// (<see cref="ISecondCredentialProvider"/>). Never the upstream proxy, never a consumer's.</summary>
    internal static bool IsMappedProvider(IAuthenticationProvider provider) => provider is ISecondCredentialProvider;

    /// <summary>The pair a second credential establishes, judged by the first of Identity's own
    /// providers that claims its issuer; <see langword="null"/> when none claims it or the one that does
    /// refuses it. Every cause is one answer to the caller and its own line in the log.</summary>
    private async Task<PrincipalId?> ValidateSecondCredentialAsync(
        string secondCredential, DateTimeOffset now, CancellationToken cancellationToken)
    {
        foreach (var provider in providers.Registered)
        {
            if (provider is not ISecondCredentialProvider candidate)
            {
                continue;
            }

            var validation = await candidate.ValidateSecondCredentialAsync(secondCredential, now, cancellationToken)
                .ConfigureAwait(false);
            if (!validation.Claimed)
            {
                continue;
            }

            if (validation.Principal is { } validated)
            {
                return validated.Id;
            }

            logger.LogInformation(
                "Second credential refused by provider {Provider}: {Cause}.", provider.Name, validation.Cause);
            return null;
        }

        logger.LogInformation("Second credential refused: no Identity provider claims its issuer.");
        return null;
    }

    /// <summary>An unlinked <see cref="PrincipalKind.Account"/> principal an Identity provider
    /// established: the mapping saw it unlinked and passed it through. A linked one already arrives as
    /// its account principal; a pair linked by a concurrent call since is caught by the link's primary
    /// key.</summary>
    private bool IsEligible(Principal principal) =>
        principal.Kind == PrincipalKind.Account
        && !string.Equals(principal.Id.Issuer, AccountId.PrincipalIssuer, StringComparison.Ordinal)
        && established.Contains(principal);
}
