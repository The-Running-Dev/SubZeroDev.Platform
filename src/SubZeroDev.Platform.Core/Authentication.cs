using SubZeroDev.Platform.Abstractions;

namespace SubZeroDev.Platform.Core;

/// <summary>A rejected authentication provider registration.</summary>
public sealed record AuthenticationProviderRegistrationError : PlatformError
{
    private AuthenticationProviderRegistrationError(string code, string detail)
        : base(code) => Detail = detail;

    /// <summary>Names the providers involved.</summary>
    public string Detail { get; }

    /// <inheritdoc/>
    public override bool IsRetryable => false;

    /// <summary>Two providers share a name. A rejection naming its source is worthless if two
    /// sources share one.</summary>
    /// <param name="name">The duplicated name.</param>
    /// <param name="first">The provider already registered under that name.</param>
    /// <param name="second">The provider that tried to register alongside it.</param>
    /// <returns>The error.</returns>
    public static AuthenticationProviderRegistrationError DuplicateProviderName(
        string name, string first, string second) =>
        new(
            nameof(DuplicateProviderName),
            $"Two authentication providers are registered under the name '{name}': '{first}' and '{second}'.");

    /// <summary>Registration was attempted after the registry was frozen.</summary>
    /// <param name="name">The provider that arrived late.</param>
    /// <returns>The error.</returns>
    public static AuthenticationProviderRegistrationError RegistryFrozen(string name) =>
        new(
            nameof(RegistryFrozen),
            $"The authentication provider registry is frozen; '{name}' cannot be registered.");
}

/// <summary>Collects authentication provider registrations. The fifth registry, and the one whose
/// emptiness the composition profile is validated against.</summary>
public interface IAuthenticationProviderRegistry
{
    /// <summary>Registers one provider.</summary>
    /// <param name="provider">The provider.</param>
    /// <returns>Success, or why the registration was rejected.</returns>
    Result<AuthenticationProviderRegistrationError> Register(IAuthenticationProvider provider);

    /// <summary>Everything registered, in registration order.</summary>
    IReadOnlyList<IAuthenticationProvider> Registered { get; }

    /// <summary>Closes the registry. One-way: registration after this is a defect, not a
    /// condition.</summary>
    void Freeze();
}

/// <inheritdoc cref="IAuthenticationProviderRegistry"/>
internal sealed class AuthenticationProviderRegistry : IAuthenticationProviderRegistry
{
    private readonly List<IAuthenticationProvider> _registered = [];
    private readonly Lock _gate = new();
    private bool _frozen;

    /// <inheritdoc/>
    public IReadOnlyList<IAuthenticationProvider> Registered => _registered;

    /// <inheritdoc/>
    public Result<AuthenticationProviderRegistrationError> Register(IAuthenticationProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        lock (_gate)
        {
            if (_frozen)
            {
                return Result<AuthenticationProviderRegistrationError>.Failure(
                    AuthenticationProviderRegistrationError.RegistryFrozen(provider.Name));
            }

            var existing = _registered.FirstOrDefault(
                registered => string.Equals(registered.Name, provider.Name, StringComparison.Ordinal));
            if (existing is not null)
            {
                return Result<AuthenticationProviderRegistrationError>.Failure(
                    AuthenticationProviderRegistrationError.DuplicateProviderName(
                        provider.Name, existing.GetType().Name, provider.GetType().Name));
            }

            _registered.Add(provider);
            return Result<AuthenticationProviderRegistrationError>.Success();
        }
    }

    /// <inheritdoc/>
    public void Freeze()
    {
        lock (_gate)
        {
            _frozen = true;
        }
    }
}

/// <summary>Runs the registered authentication providers in registration order and produces the
/// request's principal.</summary>
/// <remarks>Order is registration order, on the same terms as the tenant resolver chain: a priority
/// number would be a second ordering that will eventually disagree with the first.
///
/// <para>The three outcomes are deliberately not two. A provider that <i>rejects</i> a presented
/// credential ends the chain — falling through to the next provider, or to
/// <see cref="Principal.Anonymous"/>, would let a forged credential succeed at being ignored, and
/// would let a caller shop a bad token around the registered providers until one shrugged. A
/// provider that establishes a principal ends it too. Only a provider that saw no credential of its
/// kind defers to the next, and a chain in which every provider defers answers
/// <see cref="Principal.Anonymous"/> — which is a success, to be denied later by authorization if it
/// is denied at all.</para></remarks>
internal sealed class AuthenticationChain(IAuthenticationProviderRegistry registry)
{
    /// <summary>Authenticates one request against the registered providers.</summary>
    /// <param name="request">The transport's credential surface.</param>
    /// <param name="cancellationToken">Cancels the authentication.</param>
    /// <returns>The established principal, or the first provider's rejection.</returns>
    public async Task<Result<Principal, AuthenticationError>> AuthenticateAsync(
        IAuthenticationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        foreach (var provider in registry.Registered)
        {
            var authenticated = await provider
                .AuthenticateAsync(request, cancellationToken)
                .ConfigureAwait(false);

            if (!authenticated.IsSuccess)
            {
                return authenticated;
            }

            if (authenticated.Value.Kind != PrincipalKind.Anonymous)
            {
                return authenticated;
            }
        }

        return Result<Principal, AuthenticationError>.Success(Principal.Anonymous);
    }
}
