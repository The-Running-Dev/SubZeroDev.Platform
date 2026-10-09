using System.Security.Cryptography;
using SubZeroDev.Platform.Abstractions;

namespace SubZeroDev.Platform.Identity;

/// <summary>An account's identity. <c>design/20-contract.md</c>, Types §16. Opaque: Identity mints it
/// from 128 random bits as 32 lowercase hexadecimal characters, and nothing parses it or derives it
/// from a claim.</summary>
/// <param name="Value">The identifier.</param>
public readonly record struct AccountId(string Value)
{
    /// <summary>The issuer half of every account principal's id. A provider-established principal
    /// carrying it is rejected, so no issuer can impersonate the account namespace.</summary>
    public const string PrincipalIssuer = "platform.account";

    /// <summary>The account's principal id: <see cref="PrincipalIssuer"/> and <see cref="Value"/>.</summary>
    /// <returns>The principal id.</returns>
    public PrincipalId ToPrincipalId() => new(PrincipalIssuer, Value);

    /// <inheritdoc/>
    public override string ToString() => Value;

    /// <summary>Mints a new identifier.</summary>
    internal static AccountId CreateNew() => new(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)));
}

/// <summary>One sign-in identity linked to an account: the (issuer, subject) pair its provider
/// established, never parsed.</summary>
/// <param name="Identity">The pair.</param>
/// <param name="LinkedAt">When it was linked.</param>
public sealed record LinkedIdentity(PrincipalId Identity, DateTimeOffset LinkedAt);

/// <summary>An account. Carries no email, display name, profile or claim, and none may be added
/// (I-I4, I-I17): the display name on an account principal comes from the credential the request
/// presented.</summary>
/// <param name="Id">The account's identity.</param>
/// <param name="CreatedAt">When it was created.</param>
/// <param name="Identities">Its linked identities, ordered by <see cref="LinkedIdentity.LinkedAt"/>,
/// then by issuer and subject ordinally. Never empty.</param>
public sealed record Account(AccountId Id, DateTimeOffset CreatedAt, IReadOnlyList<LinkedIdentity> Identities);

/// <summary>The account API — <c>design/20-contract.md</c>, Public surface §19. Present only when the
/// host registers <see cref="IdentityAccountsModule"/>. The ambient principal is the caller.</summary>
public interface IAccountApi
{
    /// <summary>Turns the ambient principal's identity into an account: writes the account, its one
    /// linked identity and a <see cref="AuditClass.Required"/> audit row in one transaction. Only an
    /// unlinked <see cref="PrincipalKind.Account"/> principal established by an Identity provider is
    /// eligible.</summary>
    /// <param name="cancellationToken">Cancels the create.</param>
    /// <returns>The account, or why it was not created.</returns>
    Task<Result<Account, AccountError>> CreateAccountAsync(CancellationToken cancellationToken);

    /// <summary>Reads the ambient account principal's account.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The account, or why it was not read.</returns>
    Task<Result<Account, AccountError>> GetCurrentAccountAsync(CancellationToken cancellationToken);

    /// <summary>Links a second sign-in to the ambient account principal's account: the only way a
    /// second identity joins an account (I-I18). <paramref name="secondCredential"/> is validated by
    /// Identity's own providers, with no mapping, and must carry an <c>iat</c> no more than five
    /// minutes before the call. The link and a <see cref="AuditClass.Required"/> audit row share one
    /// transaction. A pair the account already holds succeeds and writes nothing.</summary>
    /// <param name="secondCredential">A compact bearer token for the second sign-in, read by the host
    /// from a request header, never from a body.</param>
    /// <param name="cancellationToken">Cancels the link.</param>
    /// <returns>The account with its identities, or why the pair was not linked.</returns>
    Task<Result<Account, AccountError>> LinkAsync(string secondCredential, CancellationToken cancellationToken);
}

/// <summary>Identity's own audited action names. Each is <see cref="AuditClass.Required"/>.</summary>
public static class IdentityAuditActions
{
    /// <summary>An account was created with its first linked identity.</summary>
    public static AuditAction AccountCreated { get; } = new("platform.identity.account-created");

    /// <summary>An identity was linked to an existing account.</summary>
    public static AuditAction IdentityLinked { get; } = new("platform.identity.identity-linked");

    /// <summary>An identity was unlinked from its account.</summary>
    public static AuditAction IdentityUnlinked { get; } = new("platform.identity.identity-unlinked");
}

/// <summary>Why an account operation did not complete. <c>design/20-contract.md</c>, Error semantics
/// §16. At authentication a store failure is not an <see cref="AccountError"/>: it is
/// <see cref="AuthenticationError.ProviderFailed"/>, and never the unlinked pair.</summary>
public sealed record AccountError : PlatformError
{
    private AccountError(string code, bool isRetryable, string detail)
        : base(code)
    {
        IsRetryable = isRetryable;
        Detail = detail;
    }

    /// <summary>A generic description. It never names a pair another account holds.</summary>
    public string Detail { get; }

    /// <inheritdoc/>
    public override bool IsRetryable { get; }

    /// <summary>The caller is not an unlinked <see cref="PrincipalKind.Account"/> principal established
    /// by an Identity provider: <c>Anonymous</c>, <c>Delegated</c>, <c>System</c>, a consumer
    /// provider's principal, or already an account principal.</summary>
    /// <returns>The error.</returns>
    public static AccountError NotEligible() =>
        new(
            nameof(NotEligible),
            isRetryable: false,
            "This principal cannot create an account.");

    /// <summary>The caller is not an account principal.</summary>
    /// <returns>The error.</returns>
    public static AccountError NotAnAccount() =>
        new(
            nameof(NotAnAccount),
            isRetryable: false,
            "This principal is not an account.");

    /// <summary>The second credential is not claimed, fails validation, has no <c>iat</c>, or was
    /// issued too long before the call. One answer for every cause; the cause goes to the log.</summary>
    /// <returns>The error.</returns>
    public static AccountError SecondCredentialRejected() =>
        new(
            nameof(SecondCredentialRejected),
            isRetryable: false,
            "The second sign-in could not be accepted. Sign in with it again.");

    /// <summary>The pair is linked to another account, including by a concurrent call that won the
    /// link's primary key. Accounts are never merged.</summary>
    /// <returns>The error.</returns>
    public static AccountError IdentityAlreadyLinked() =>
        new(
            nameof(IdentityAlreadyLinked),
            isRetryable: false,
            "This sign-in already belongs to an account.");

    /// <summary>The unlink names the account's only identity.</summary>
    /// <returns>The error.</returns>
    public static AccountError LastIdentity() =>
        new(
            nameof(LastIdentity),
            isRetryable: false,
            "An account keeps at least one sign-in.");

    /// <summary>The unlink names a pair this account does not hold, whether or not another account
    /// holds it.</summary>
    /// <returns>The error.</returns>
    public static AccountError IdentityNotLinked() =>
        new(
            nameof(IdentityNotLinked),
            isRetryable: false,
            "This account holds no such sign-in.");

    /// <summary>The account store could not complete the operation. Retryable.</summary>
    /// <returns>The error.</returns>
    public static AccountError StoreUnavailable() =>
        new(
            nameof(StoreUnavailable),
            isRetryable: true,
            "The account store could not complete the operation. Retry.");
}
