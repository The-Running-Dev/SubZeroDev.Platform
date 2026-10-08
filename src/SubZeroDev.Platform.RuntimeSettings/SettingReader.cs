using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Persistence;

namespace SubZeroDev.Platform.RuntimeSettings;

/// <summary>Reads the value of a runtime setting that applies to the ambient principal and tenant.</summary>
public interface ISettingReader
{
    /// <summary>Resolves user, then tenant, then global, then the declared default, over the layers
    /// that exist for the ambient operation and that the declaration admits (I-ST1). Reads the store
    /// every time (I-ST7).</summary>
    /// <typeparam name="T">The setting's value type.</typeparam>
    /// <param name="setting">The setting. Looked up by name in the frozen catalogue, whose declaration
    /// is the one applied.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The value and the layer that held it, or why it could not be read.</returns>
    Task<Result<ResolvedSetting<T>, SettingError>> GetAsync<T>(
        SettingDefinition<T> setting, CancellationToken cancellationToken)
        where T : notnull;
}

/// <inheritdoc cref="ISettingReader"/>
internal sealed class SettingReader(
    SettingCatalogue catalogue,
    SettingStore store,
    IUnitOfWork unitOfWork,
    ICurrentTenant currentTenant,
    ICurrentPrincipal currentPrincipal) : ISettingReader
{
    /// <summary>Highest precedence first.</summary>
    private static readonly SettingLayer[] Precedence = [SettingLayer.User, SettingLayer.Tenant, SettingLayer.Global];

    public async Task<Result<ResolvedSetting<T>, SettingError>> GetAsync<T>(
        SettingDefinition<T> setting, CancellationToken cancellationToken)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(setting);

        if (catalogue.Find(setting.Name) is not SettingDefinition<T> declared)
        {
            return Result<ResolvedSetting<T>, SettingError>.Failure(SettingError.NotDeclared(setting.Name));
        }

        var tenant = currentTenant.Current;
        var principal = currentPrincipal.Current;
        var layers = SettingLayers.Existing(tenant, principal, declared.Layers);
        if (layers.Count == 0)
        {
            return Result<ResolvedSetting<T>, SettingError>.Success(new ResolvedSetting<T>(declared.Default, null));
        }

        var read = await unitOfWork.ExecuteAsync(
            TransactionIntent.ReadOnly,
            ct => store.ReadAsync(declared.Name, layers, tenant, principal.Id, ct),
            cancellationToken).ConfigureAwait(false);

        if (!read.IsSuccess)
        {
            return Result<ResolvedSetting<T>, SettingError>.Failure(SettingError.StoreUnavailable(declared.Name));
        }

        foreach (var layer in Precedence)
        {
            if (!read.Value.TryGetValue(layer, out var stored))
            {
                continue;
            }

            // The first layer holding a row answers. A value it holds that no longer fits the
            // declaration is an error, never a fall-through to a lower layer or the default (I-ST9).
            return declared.TryParse(stored, out var value) && declared.IsValid(value)
                ? Result<ResolvedSetting<T>, SettingError>.Success(new ResolvedSetting<T>(value, layer))
                : Result<ResolvedSetting<T>, SettingError>.Failure(SettingError.StoredValueInvalid(declared.Name, layer));
        }

        return Result<ResolvedSetting<T>, SettingError>.Success(new ResolvedSetting<T>(declared.Default, null));
    }
}

/// <summary>Which layers exist for an operation (<c>design/10-design.md</c>, Data model §13).</summary>
internal static class SettingLayers
{
    /// <summary>The layers that both exist for the tenant and principal and are admitted.</summary>
    internal static IReadOnlySet<SettingLayer> Existing(
        TenantId tenant, Principal principal, IReadOnlySet<SettingLayer> admitted)
    {
        var existing = new HashSet<SettingLayer>();
        foreach (var layer in admitted)
        {
            if (Exists(layer, tenant, principal))
            {
                existing.Add(layer);
            }
        }

        return existing;
    }

    /// <summary>Global always exists; tenant only for a non-implicit tenant; user only for an
    /// <see cref="PrincipalKind.Account"/> in a non-implicit tenant (I-ST1).</summary>
    internal static bool Exists(SettingLayer layer, TenantId tenant, Principal principal) => layer switch
    {
        SettingLayer.Global => true,
        SettingLayer.Tenant => tenant != TenantId.Implicit,
        SettingLayer.User => tenant != TenantId.Implicit && principal.Kind == PrincipalKind.Account,
        _ => false,
    };
}
