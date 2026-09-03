using System.Reflection;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;

namespace SubZeroDev.Platform.Tests;

/// <summary>D5-S8's authentication seam: the three outcomes, and the shape of what a provider is
/// allowed to see.</summary>
public sealed class AuthenticationTests
{
    /// <summary>S8.9, first half — no credential presented is success carrying
    /// <see cref="Principal.Anonymous"/>, not a failure. An absent token and a forged one must not
    /// be the same answer, and this is the half that says an absent one is fine.</summary>
    [Fact]
    public async Task No_credential_presented_succeeds_carrying_anonymous()
    {
        var chain = Chain(new StubAuthenticationProvider("bearer"));

        var authenticated = await chain.AuthenticateAsync(
            new StubAuthenticationRequest(), CancellationToken.None);

        Assert.True(authenticated.IsSuccess);
        Assert.Equal(Principal.Anonymous, authenticated.Value);
    }

    /// <summary>S8.9, second half — an invalid credential fails, and the chain does not fall back to
    /// <see cref="Principal.Anonymous"/>. Registering a second provider that would have deferred is
    /// what makes the absence of a fallback observable rather than incidental: a forged credential
    /// must not be able to shop itself around the registry until one provider shrugs.</summary>
    [Fact]
    public async Task An_invalid_credential_is_rejected_and_never_falls_back_to_anonymous()
    {
        var wouldDefer = new StubAuthenticationProvider("second");
        var chain = Chain(
            new StubAuthenticationProvider(
                "bearer",
                _ => Result<Principal, AuthenticationError>.Failure(
                    AuthenticationError.CredentialRejected("bearer"))),
            wouldDefer);

        var authenticated = await chain.AuthenticateAsync(
            new StubAuthenticationRequest(("Authorization", "Bearer forged")), CancellationToken.None);

        Assert.False(authenticated.IsSuccess);
        Assert.Equal(nameof(AuthenticationError.CredentialRejected), authenticated.Error.Code);
        Assert.Equal(0, wouldDefer.CallCount);
    }

    /// <summary>S8.10 — no signing key cached is an authentication failure with its own code, not a
    /// fault and not a fallback to anonymous. That it is <em>not</em> retryable is the part that
    /// matters: Platform retries nothing on the request path, so a caller is told the truth rather
    /// than invited to hammer a provider whose key material will arrive on its own schedule.</summary>
    [Fact]
    public async Task No_cached_key_material_fails_the_request_as_an_authentication_failure()
    {
        var chain = Chain(new StubAuthenticationProvider(
            "bearer",
            _ => Result<Principal, AuthenticationError>.Failure(
                AuthenticationError.KeyMaterialUnavailable("bearer"))));

        var authenticated = await chain.AuthenticateAsync(
            new StubAuthenticationRequest(("Authorization", "Bearer valid-looking")),
            CancellationToken.None);

        Assert.False(authenticated.IsSuccess);
        Assert.Equal(nameof(AuthenticationError.KeyMaterialUnavailable), authenticated.Error.Code);
        Assert.False(authenticated.Error.IsRetryable);
        Assert.NotEqual(nameof(AuthenticationError.CredentialRejected), authenticated.Error.Code);
    }

    /// <summary>S8.11 — <see cref="IAuthenticationRequest"/> exposes headers and nothing else,
    /// asserted over its members. A credential in a body is a credential in a log, and the only way
    /// to keep that true is for there to be no route to one.</summary>
    [Fact]
    public void The_authentication_request_exposes_headers_and_nothing_else()
    {
        var members = typeof(IAuthenticationRequest)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(member => member is not MethodInfo { IsSpecialName: true })
            .ToList();

        var only = Assert.Single(members);
        Assert.Equal(nameof(IAuthenticationRequest.Headers), only.Name);

        var headers = Assert.IsAssignableFrom<PropertyInfo>(only);
        Assert.Equal(
            typeof(IReadOnlyDictionary<string, IReadOnlyList<string>>),
            headers.PropertyType);
        Assert.Null(headers.SetMethod);
    }

    /// <summary>The chain runs providers in registration order and the first to establish a
    /// principal ends it — order is registration order, never a priority number, which would be a
    /// second ordering that eventually disagrees with the first.</summary>
    [Fact]
    public async Task Providers_run_in_registration_order_and_the_first_to_establish_wins()
    {
        var established = new Principal(
            new PrincipalId("issuer", "subject"), PrincipalKind.Account, "Subject", null);

        var second = new StubAuthenticationProvider(
            "second", _ => Result<Principal, AuthenticationError>.Success(established));
        var third = new StubAuthenticationProvider("third");

        var chain = Chain(new StubAuthenticationProvider("first"), second, third);

        var authenticated = await chain.AuthenticateAsync(
            new StubAuthenticationRequest(), CancellationToken.None);

        Assert.True(authenticated.IsSuccess);
        Assert.Equal(established, authenticated.Value);
        Assert.Equal(1, second.CallCount);
        Assert.Equal(0, third.CallCount);
    }

    /// <summary>With no provider registered — every <c>Local</c> host, and an <c>Operated</c> one
    /// before Identity lands — the chain answers <see cref="Principal.Anonymous"/> rather than
    /// failing. Step 1 is taken, not skipped.</summary>
    [Fact]
    public async Task An_empty_registry_answers_anonymous_rather_than_failing()
    {
        var authenticated = await Chain().AuthenticateAsync(
            new StubAuthenticationRequest(), CancellationToken.None);

        Assert.True(authenticated.IsSuccess);
        Assert.Equal(Principal.Anonymous, authenticated.Value);
    }

    /// <summary>Two providers sharing a name is a registration failure: a rejection naming its
    /// source is worthless if two sources share one.</summary>
    [Fact]
    public void Two_providers_sharing_a_name_are_rejected()
    {
        var registry = new AuthenticationProviderRegistry();

        Assert.True(registry.Register(new StubAuthenticationProvider("bearer")).IsSuccess);

        var duplicate = registry.Register(new StubAuthenticationProvider("bearer"));

        Assert.False(duplicate.IsSuccess);
        Assert.Equal(
            nameof(AuthenticationProviderRegistrationError.DuplicateProviderName),
            duplicate.Error.Code);
    }

    private static AuthenticationChain Chain(params IAuthenticationProvider[] providers)
    {
        var registry = new AuthenticationProviderRegistry();
        foreach (var provider in providers)
        {
            Assert.True(registry.Register(provider).IsSuccess);
        }

        registry.Freeze();
        return new AuthenticationChain(registry);
    }
}
