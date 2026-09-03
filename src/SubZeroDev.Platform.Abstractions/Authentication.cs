namespace SubZeroDev.Platform.Abstractions;

/// <summary>The transport's credential surface, and the whole of it: headers, and nothing else.
/// There is deliberately no route to a request body — a credential in a body is a credential in a
/// log, and the only way to keep that true is for the shape to make it unreachable rather than for
/// every provider to remember not to look.</summary>
public interface IAuthenticationRequest
{
    /// <summary>The request's headers, keyed case-insensitively as the transports Platform fronts
    /// already treat them. A header absent from the request is absent from this, never an empty
    /// value: "not presented" and "presented empty" are different credentials.</summary>
    IReadOnlyDictionary<string, IReadOnlyList<string>> Headers { get; }
}

/// <summary>Establishes a principal at the transport boundary. Registered by the Identity module;
/// absent in the <see cref="CompositionProfile.Local"/> profile.</summary>
public interface IAuthenticationProvider
{
    /// <summary>The provider's name, unique within the registry.</summary>
    string Name { get; }

    /// <summary>Authenticates one request. Distinguishes "no credential presented" — success
    /// carrying <see cref="Principal.Anonymous"/> — from "a credential was presented and failed to
    /// validate", which is a failure: collapsing the two makes an absent token indistinguishable
    /// from a forged one.
    ///
    /// <para>Must never block on a network fetch. Key material is fetched at startup and cached; a
    /// request arriving when no key is cached answers
    /// <see cref="AuthenticationError.KeyMaterialUnavailable"/>, which is an authentication failure
    /// and never a server error.</para></summary>
    /// <param name="request">The transport's credential surface.</param>
    /// <param name="cancellationToken">Cancels the authentication.</param>
    /// <returns>The principal, or why the presented credential was not accepted.</returns>
    Task<Result<Principal, AuthenticationError>> AuthenticateAsync(
        IAuthenticationRequest request,
        CancellationToken cancellationToken);
}

/// <summary>Why a presented credential did not establish a principal. <b>No credential presented is
/// not here</b>: that is success carrying <see cref="Principal.Anonymous"/>.</summary>
/// <remarks>None is retryable. Platform itself retries nothing on the request path —
/// <see cref="PlatformError.IsRetryable"/> is the caller's signal, not an instruction Platform
/// follows.</remarks>
public sealed record AuthenticationError : PlatformError
{
    private AuthenticationError(string code, string detail)
        : base(code) => Detail = detail;

    /// <summary>Names the cause.</summary>
    public string Detail { get; }

    /// <inheritdoc/>
    public override bool IsRetryable => false;

    /// <summary>A credential was presented and failed to validate. The caller returns
    /// unauthenticated and <b>does not fall back to <see cref="Principal.Anonymous"/></b>: a forged
    /// token that degrades into an anonymous request is a forged token that succeeded at being
    /// ignored.</summary>
    /// <param name="provider">The provider that rejected it.</param>
    /// <returns>The error.</returns>
    public static AuthenticationError CredentialRejected(string provider) =>
        new(
            nameof(CredentialRejected),
            $"Authentication provider '{provider}' rejected the presented credential.");

    /// <summary>No signing key is cached and none may be fetched on the request path. Surfaces as
    /// unauthenticated, never as a server error, and issues no outbound call: a provider that
    /// fetched key material here would turn every request into a dependency on the issuer being
    /// reachable.</summary>
    /// <param name="provider">The provider with no cached key material.</param>
    /// <returns>The error.</returns>
    public static AuthenticationError KeyMaterialUnavailable(string provider) =>
        new(
            nameof(KeyMaterialUnavailable),
            $"Authentication provider '{provider}' has no cached key material, and none may be "
            + "fetched on the request path.");

    /// <summary>The provider itself faulted. The caller returns unauthenticated and degrades
    /// readiness.</summary>
    /// <param name="provider">The provider that faulted.</param>
    /// <returns>The error.</returns>
    public static AuthenticationError ProviderFailed(string provider) =>
        new(nameof(ProviderFailed), $"Authentication provider '{provider}' failed.");
}
