using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Persistence;

namespace SubZeroDev.Platform.Identity;

/// <summary>The optional account store: <see cref="IAccountApi"/>, the mapping from a linked identity
/// to its account's principal, and the two tables it owns (<c>design/20-contract.md</c>, Public surface
/// §19). A host that does not register it has no Identity table and unchanged principals (I-I21).
/// Depends on <see cref="IdentityModule"/>; registering it alone fails startup through the module
/// graph.</summary>
public sealed class IdentityAccountsModule : IPlatformModule
{
    /// <inheritdoc/>
    public ModuleName Name { get; } = new("IdentityAccounts");

    /// <inheritdoc/>
    public IReadOnlyCollection<ModuleName> DependsOn { get; } = [new ModuleName("Identity")];

    /// <inheritdoc/>
    public void Register(IServiceCollection services)
    {
        services.TryAddSingleton<AccountStore>();
        services.TryAddSingleton<EstablishedPrincipals>();
        services.TryAddSingleton<IAccountApi, AccountApi>();

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPrincipalMapping, AccountPrincipalMapping>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IModuleMigrationSource, IdentityAccountsMigrationSource>());
    }
}
