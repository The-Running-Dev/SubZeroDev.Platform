using SubZeroDev.Platform.Abstractions;

namespace SubZeroDev.Platform.Core;

/// <summary>A rejected tenant resolver registration.</summary>
public sealed record TenantResolverRegistrationError : PlatformError
{
    private TenantResolverRegistrationError(string code, string detail)
        : base(code) => Detail = detail;

    /// <summary>Names the resolvers involved.</summary>
    public string Detail { get; }

    /// <inheritdoc/>
    public override bool IsRetryable => false;

    /// <summary>Two resolvers share a name.</summary>
    /// <param name="name">The duplicated name.</param>
    /// <param name="first">The resolver already registered under that name.</param>
    /// <param name="second">The resolver that tried to register alongside it.</param>
    /// <returns>The error.</returns>
    public static TenantResolverRegistrationError DuplicateProviderName(string name, string first, string second) =>
        new(
            nameof(DuplicateProviderName),
            $"Two tenant resolvers are registered under the name '{name}': '{first}' and '{second}'.");

    /// <summary>Registration was attempted after the registry was frozen.</summary>
    /// <param name="name">The resolver that arrived late.</param>
    /// <returns>The error.</returns>
    public static TenantResolverRegistrationError RegistryFrozen(string name) =>
        new(nameof(RegistryFrozen), $"The tenant resolver registry is frozen; '{name}' cannot be registered.");
}

/// <summary>Collects tenant resolver registrations.</summary>
public interface ITenantResolverRegistry
{
    /// <summary>Registers one resolver.</summary>
    /// <param name="resolver">The resolver.</param>
    /// <returns>Success, or why the registration was rejected.</returns>
    Result<TenantResolverRegistrationError> Register(ITenantResolver resolver);

    /// <summary>Everything registered, in registration order — the order the resolution chain
    /// consults them in.</summary>
    IReadOnlyList<ITenantResolver> Registered { get; }

    /// <summary>Closes the registry. One-way: registration after this is a defect, not a
    /// condition.</summary>
    void Freeze();
}

/// <inheritdoc cref="ITenantResolverRegistry"/>
internal sealed class TenantResolverRegistry : ITenantResolverRegistry
{
    private readonly List<ITenantResolver> _registered = [];
    private readonly Lock _gate = new();
    private bool _frozen;

    /// <inheritdoc/>
    public IReadOnlyList<ITenantResolver> Registered => _registered;

    /// <inheritdoc/>
    public Result<TenantResolverRegistrationError> Register(ITenantResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);

        lock (_gate)
        {
            if (_frozen)
            {
                return Result<TenantResolverRegistrationError>.Failure(
                    TenantResolverRegistrationError.RegistryFrozen(resolver.Name));
            }

            var existing = _registered.FirstOrDefault(
                registered => string.Equals(registered.Name, resolver.Name, StringComparison.Ordinal));
            if (existing is not null)
            {
                return Result<TenantResolverRegistrationError>.Failure(
                    TenantResolverRegistrationError.DuplicateProviderName(
                        resolver.Name, existing.GetType().Name, resolver.GetType().Name));
            }

            _registered.Add(resolver);
            return Result<TenantResolverRegistrationError>.Success();
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

/// <summary>Runs the registered resolvers in registration order and takes the first non-null
/// answer. With none registered, or every one deferring, the request proceeds in
/// <see cref="TenantId.Implicit"/> — never a failure, since a resolver has no decision type to
/// fail with.</summary>
internal sealed class TenantResolutionChain(ITenantResolverRegistry resolvers)
{
    public async Task<TenantId> ResolveAsync(CancellationToken cancellationToken)
    {
        foreach (var resolver in resolvers.Registered)
        {
            var answer = await resolver.ResolveAsync(cancellationToken).ConfigureAwait(false);
            if (answer is { } tenant)
            {
                return tenant;
            }
        }

        return TenantId.Implicit;
    }
}
