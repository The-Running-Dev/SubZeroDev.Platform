using SubZeroDev.Platform.Abstractions;

namespace SubZeroDev.Platform.Organizations;

/// <summary>Holds the active organization's tenant for the current asynchronous flow. Not a stored
/// preference and there is no column for one (<c>design/20-contract.md</c>, Types §7): the active
/// organization is a per-request selection, carried the same way <c>AmbientTransactionState</c>
/// carries the ambient transaction — an <see cref="AsyncLocal{T}"/>, set by
/// <see cref="IOrganizationApi.SwitchActiveOrganizationAsync"/> and read back by
/// <see cref="OrganizationsTenantResolver"/> (S10.6, S10.8).</summary>
public sealed class ActiveOrganizationState
{
    private readonly AsyncLocal<TenantId?> _current = new();

    /// <summary>The active organization's tenant for the current asynchronous flow, or null when
    /// none has been selected.</summary>
    public TenantId? Current
    {
        get => _current.Value;
        set => _current.Value = value;
    }
}

/// <summary>Organizations' own <see cref="ITenantResolver"/>. With no active organization selected it
/// defers, and the request proceeds in <see cref="TenantId.Implicit"/> (S10.8) — a resolver never
/// denies (I-T8).</summary>
internal sealed class OrganizationsTenantResolver(ActiveOrganizationState state) : ITenantResolver
{
    public string Name => "Organizations.ActiveOrganization";

    public Task<TenantId?> ResolveAsync(CancellationToken cancellationToken) => Task.FromResult(state.Current);
}
