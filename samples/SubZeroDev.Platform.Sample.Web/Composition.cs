using SubZeroDev.Platform.Abstractions;

namespace SubZeroDev.Platform.Sample.Web;

/// <summary>What a consumer declaring <see cref="CompositionProfile.Operated"/> owes the profile,
/// written the way a consumer writes it — in the sample, not in a framework package.</summary>
/// <remarks>D5-S8 turns the profile from a claim into a checked fact: an operated host with no
/// authentication provider (I-C1) or no sink declaring <c>IsDurable</c> (I-C2) refuses to start.
/// Both of these are the smallest honest thing that satisfies the rule, and both are replaced
/// rather than kept: S9 registers the Identity module and a test issuer in place of the provider,
/// and S13 registers the audit store module's sink in place of this one. Keeping them until then is
/// what lets the operated sample run while the gate that made them necessary stays real — the
/// alternative was degrading the host, which the profile exists to rule out.</remarks>
public static class OperatedComposition
{
    /// <summary>An authentication provider that reads no credential and therefore establishes
    /// none — every request continues as <see cref="Principal.Anonymous"/>, to be denied later by
    /// authorization if it is denied at all. It is deliberately not a stub that fabricates a
    /// principal: a sample that authenticates nobody is honest, and one that authenticates everybody
    /// is the "registered check that always passes" the brief refuses.</summary>
    public sealed class NoCredentialAuthenticationProvider : IAuthenticationProvider
    {
        public string Name => "Sample.NoCredential";

        public Task<Result<Principal, AuthenticationError>> AuthenticateAsync(
            IAuthenticationRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(Result<Principal, AuthenticationError>.Success(Principal.Anonymous));
    }

    /// <summary>An audit sink that appends to a file the operator names, and declares
    /// <see cref="IsDurable"/> because a file survives a restart. Not the audit store: it has no
    /// read API, no tenant scoping and no indexes — it is the least a deployment can offer and still
    /// truthfully claim its audit trail outlives the process. S13 replaces it.</summary>
    public sealed class FileAuditSink(string path) : IAuditSink
    {
        // Two concurrent appends to one file interleave partial lines, so writes are serialised.
        // A store-backed sink writes rows and needs none of this, which is part of why this one is
        // the placeholder rather than the destination.
        private readonly SemaphoreSlim _gate = new(1, 1);

        public string Name => "Sample.FileAudit";

        public bool IsDurable => true;

        public async Task<Result<AuditError>> WriteAsync(
            AuditEvent auditEvent, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(auditEvent);

            // The record's own fields only. There is no payload, changed-field list or free-form
            // detail to render because AuditEvent declares none — structural, rather than a rule
            // this sink has to remember. The actor is written as two fields, never as the rendered
            // pair split back apart (I-I3).
            var line = string.Join(
                '\t',
                auditEvent.OccurredAt.ToString("O"),
                auditEvent.Tenant,
                auditEvent.Actor.Issuer,
                auditEvent.Actor.Subject,
                auditEvent.ActorKind,
                auditEvent.Action,
                auditEvent.Outcome,
                auditEvent.Class,
                auditEvent.Correlation);

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await File.AppendAllTextAsync(path, line + System.Environment.NewLine, cancellationToken)
                    .ConfigureAwait(false);

                return Result<AuditError>.Success();
            }
            catch (IOException)
            {
                // The class rule decides the consequence and the sink does not choose it: this says
                // only that it could not write, and the caller applies Required or Recorded.
                // Retryable, because a locked or full file is a condition that clears.
                return Result<AuditError>.Failure(AuditError.SinkUnavailable(Name));
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    /// <summary>The permission names this sample's own endpoints declare (D5-S8: every endpoint
    /// mapped through the pipeline now carries a requirement or a named exemption). Declared here
    /// rather than inline at the map call so the catalog and the provider below can both reach
    /// them.</summary>
    public static class SamplePermissions
    {
        /// <summary>Reads the sample's root diagnostic response.</summary>
        public static PermissionName ReadRoot { get; } = new("Sample.Web.ReadRoot");

        /// <summary>Creates a catalogue item and an order in one transaction.</summary>
        public static PermissionName CreateOrder { get; } = new("Sample.Web.CreateOrder");
    }

    /// <summary><see cref="SamplePermissions"/> declared as a catalog, so a typo here fails startup
    /// the same way any module's would.</summary>
    public sealed class SamplePermissionCatalog : IPermissionCatalog
    {
        public IReadOnlyCollection<PermissionName> Declares { get; } =
        [
            SamplePermissions.ReadRoot,
            SamplePermissions.CreateOrder,
        ];
    }

    /// <summary>No real permission policy exists in this sample — Organizations, which ships the
    /// second of D5's exactly two permission providers, is S10. Granting this sample's own declared
    /// permissions to any principal is the smallest honest thing that satisfies I-R6 without
    /// pretending a policy has been decided: it is not a role-assignment table, it grants nothing
    /// beyond what this sample itself declares, and it is replaced rather than kept once S10 lands.</summary>
    public sealed class NoPolicyPermissionProvider : IPermissionProvider
    {
        private static readonly IReadOnlySet<PermissionName> Granted = new HashSet<PermissionName>
        {
            SamplePermissions.ReadRoot,
            SamplePermissions.CreateOrder,
        };

        public PermissionProviderName Name { get; } = new("Sample.NoPolicy");

        public Task<Result<IReadOnlySet<PermissionName>, AuthorizationError>> GrantsAsync(
            Principal principal, TenantId tenant, ResourceRef? resource, CancellationToken cancellationToken) =>
            Task.FromResult(Result<IReadOnlySet<PermissionName>, AuthorizationError>.Success(Granted));
    }
}
