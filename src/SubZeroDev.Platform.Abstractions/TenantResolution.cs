namespace SubZeroDev.Platform.Abstractions;

/// <summary>Resolves the request's tenant. With none registered the answer is
/// <see cref="TenantId.Implicit"/>, which is what makes single-tenant deployment identical to
/// today rather than a special case.</summary>
public interface ITenantResolver
{
    /// <summary>The resolver's name, unique among registered resolvers.</summary>
    string Name { get; }

    /// <summary>The tenant, or <see langword="null"/> to defer to the next resolver. A resolver
    /// never denies — a resolver that means "this principal may not use that tenant" defers, and
    /// the request proceeds in the implicit tenant to be denied by authorization, never here.</summary>
    /// <param name="cancellationToken">Cancels the resolution.</param>
    /// <returns>The tenant, or <see langword="null"/> to defer.</returns>
    Task<TenantId?> ResolveAsync(CancellationToken cancellationToken);
}
