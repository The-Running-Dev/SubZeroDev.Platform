using Microsoft.Extensions.DependencyInjection;
using SubZeroDev.Platform.Abstractions;

namespace SubZeroDev.Platform.Identity;

/// <summary>D5-S9's module: authentication providers and the mapping from an authentication result
/// to a principal. Owns no entity type, no context and no migration — Identity is not a directory
/// (<c>20-contract.md</c> §1), and a consumer never registers this module without also registering
/// at least one <see cref="IAuthenticationProvider"/> naming an issuer it trusts, which this type
/// deliberately does not do on the consumer's behalf.</summary>
public sealed class IdentityModule : IPlatformModule
{
    /// <inheritdoc/>
    public ModuleName Name { get; } = new("Identity");

    /// <inheritdoc/>
    public IReadOnlyCollection<ModuleName> DependsOn { get; } = [];

    /// <inheritdoc/>
    public void Register(IServiceCollection services)
    {
        // Nothing to contribute unconditionally: the module has no rows, no defaults and no
        // fallback provider. A host that registers this module and no IAuthenticationProvider is
        // still an Operated host with no authentication provider registered, and I-C1 (D5-S8)
        // refuses to start it rather than this module quietly supplying one.
    }
}
