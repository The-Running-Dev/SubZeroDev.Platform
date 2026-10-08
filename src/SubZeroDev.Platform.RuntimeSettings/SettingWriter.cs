using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Persistence;

namespace SubZeroDev.Platform.RuntimeSettings;

/// <summary>Changes the value of a runtime setting at one layer.</summary>
public interface ISettingWriter
{
    /// <summary>Stores <paramref name="value"/> at <paramref name="layer"/> for the ambient operation:
    /// the installation's row, the current tenant's, or the ambient account's own. Checks, in order,
    /// that the setting is declared, the layer is admitted, the layer exists, the value is valid and
    /// the permission is held, then writes the row and its audit record in one transaction (I-ST3,
    /// I-ST4).</summary>
    /// <typeparam name="T">The setting's value type.</typeparam>
    /// <param name="setting">The setting. Looked up by name in the frozen catalogue, whose declaration
    /// is the one applied.</param>
    /// <param name="layer">The layer written.</param>
    /// <param name="value">The value.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Success once committed, or why nothing changed.</returns>
    Task<Result<SettingError>> SetAsync<T>(
        SettingDefinition<T> setting, SettingLayer layer, T value, CancellationToken cancellationToken)
        where T : notnull;

    /// <summary>Removes the row at <paramref name="layer"/>, so the next read falls through to the
    /// layer below. Checks as <see cref="SetAsync{T}"/> does, without the value. A clear that finds no
    /// row succeeds and writes no audit record.</summary>
    /// <param name="setting">The setting.</param>
    /// <param name="layer">The layer cleared.</param>
    /// <param name="cancellationToken">Cancels the clear.</param>
    /// <returns>Success once committed, or why nothing changed.</returns>
    Task<Result<SettingError>> ClearAsync(
        SettingDefinition setting, SettingLayer layer, CancellationToken cancellationToken);
}

/// <summary>Declares the two write permissions, so the evaluator accepts them.</summary>
internal sealed class RuntimeSettingsPermissionCatalog : IPermissionCatalog
{
    public IReadOnlyCollection<PermissionName> Declares { get; } =
        [RuntimeSettingsPermissions.WriteGlobal, RuntimeSettingsPermissions.WriteTenant];
}

/// <inheritdoc cref="ISettingWriter"/>
internal sealed class SettingWriter(
    SettingCatalogue catalogue,
    SettingStore store,
    IUnitOfWork unitOfWork,
    ICurrentTenant currentTenant,
    ICurrentPrincipal currentPrincipal,
    IAuthorizationEvaluator evaluator,
    IAuditWriter auditWriter) : ISettingWriter
{
    public async Task<Result<SettingError>> SetAsync<T>(
        SettingDefinition<T> setting, SettingLayer layer, T value, CancellationToken cancellationToken)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(setting);

        if (catalogue.Find(setting.Name) is not SettingDefinition<T> declared)
        {
            return Result<SettingError>.Failure(SettingError.NotDeclared(setting.Name));
        }

        var (row, unavailable) = Locate(declared, layer);
        if (unavailable is not null)
        {
            return Result<SettingError>.Failure(unavailable);
        }

        if (!declared.IsValid(value))
        {
            return Result<SettingError>.Failure(SettingError.InvalidValue(declared.Name, layer));
        }

        if (await AuthorizeAsync(declared.Name, layer, cancellationToken).ConfigureAwait(false) is { } refused)
        {
            return Result<SettingError>.Failure(refused);
        }

        var stored = declared.Format(value);
        var written = await unitOfWork.ExecuteAsync(
            TransactionIntent.Write,
            async ct =>
            {
                await store.UpsertAsync(row, stored, ct).ConfigureAwait(false);
                await AuditAsync(RuntimeSettingsAuditActions.SettingSet, row, ct).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);

        return written.IsSuccess
            ? Result<SettingError>.Success()
            : Result<SettingError>.Failure(SettingError.StoreUnavailable(declared.Name));
    }

    public async Task<Result<SettingError>> ClearAsync(
        SettingDefinition setting, SettingLayer layer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(setting);

        // A definition of another type under a declared name is not the declared setting.
        if (catalogue.Find(setting.Name) is not { } declared || declared.GetType() != setting.GetType())
        {
            return Result<SettingError>.Failure(SettingError.NotDeclared(setting.Name));
        }

        var (row, unavailable) = Locate(declared, layer);
        if (unavailable is not null)
        {
            return Result<SettingError>.Failure(unavailable);
        }

        if (await AuthorizeAsync(declared.Name, layer, cancellationToken).ConfigureAwait(false) is { } refused)
        {
            return Result<SettingError>.Failure(refused);
        }

        var cleared = await unitOfWork.ExecuteAsync(
            TransactionIntent.Write,
            async ct =>
            {
                // An absent row is not a change, so it is not audited.
                if (await store.DeleteAsync(row, ct).ConfigureAwait(false))
                {
                    await AuditAsync(RuntimeSettingsAuditActions.SettingCleared, row, ct).ConfigureAwait(false);
                }
            },
            cancellationToken).ConfigureAwait(false);

        return cleared.IsSuccess
            ? Result<SettingError>.Success()
            : Result<SettingError>.Failure(SettingError.StoreUnavailable(declared.Name));
    }

    /// <summary>The row a write at <paramref name="layer"/> reaches, or why there is none: the
    /// declaration does not admit the layer, or the layer does not exist for the ambient operation
    /// (I-ST1). A global row lives under the implicit tenant, tenant and user rows only under the
    /// current one (I-ST5), and a user row is only the ambient account's own (I-ST3).</summary>
    private (SettingRowKey Row, SettingError? Error) Locate(SettingDefinition declared, SettingLayer layer)
    {
        if (!declared.Layers.Contains(layer))
        {
            return (default, SettingError.LayerNotAdmitted(declared.Name, layer));
        }

        var tenant = currentTenant.Current;
        var principal = currentPrincipal.Current;
        if (!SettingLayers.Exists(layer, tenant, principal))
        {
            return (default, SettingError.LayerUnavailable(declared.Name, layer));
        }

        var row = layer switch
        {
            SettingLayer.Global => new SettingRowKey(declared.Name, layer, TenantId.Implicit, null),
            SettingLayer.Tenant => new SettingRowKey(declared.Name, layer, tenant, null),
            _ => new SettingRowKey(declared.Name, layer, tenant, principal.Id),
        };
        return (row, null);
    }

    /// <summary>Evaluates the layer's permission, scoped to the setting, before the store is touched
    /// (I-ST3). A user-layer write needs none. A denial the evaluator could not fully decide or record
    /// is retryable; any other denial the evaluator has already audited.</summary>
    private async Task<SettingError?> AuthorizeAsync(
        SettingName name, SettingLayer layer, CancellationToken cancellationToken)
    {
        var permission = layer switch
        {
            SettingLayer.Global => RuntimeSettingsPermissions.WriteGlobal,
            SettingLayer.Tenant => RuntimeSettingsPermissions.WriteTenant,
            _ => (PermissionName?)null,
        };

        if (permission is not { } required)
        {
            return null;
        }

        var decision = await evaluator.EvaluateAsync(
            required, new ResourceRef("RuntimeSetting", name.Value), cancellationToken).ConfigureAwait(false);

        if (decision.Outcome == AuthorizationOutcome.Allowed)
        {
            return null;
        }

        return decision.AuditFailure is not null || decision.ProviderFailure is not null
            ? SettingError.AuthorizationUnavailable(name, layer)
            : SettingError.PermissionDenied(name, layer);
    }

    /// <summary>The change's one <see cref="AuditClass.Required"/> record, naming the layer and the
    /// setting and never the value (I-ST4). Enlisted on the change's transaction, so a record that
    /// cannot be written rolls the change back.</summary>
    private Task AuditAsync(AuditAction action, SettingRowKey row, CancellationToken cancellationToken) =>
        auditWriter.WriteAsync(
            action,
            new ResourceRef("RuntimeSetting", $"{SettingStore.FormatLayer(row.Layer)}/{row.Name.Value}"),
            AuditOutcome.Allowed,
            AuditClass.Required,
            cancellationToken);
}
