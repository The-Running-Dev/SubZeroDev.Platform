using SubZeroDev.Platform.Abstractions;

namespace SubZeroDev.Platform.Testing;

/// <summary>A clock a test moves. Every persisted instant and every SQL comparand originates from
/// <see cref="IClock"/>, so advancing this one moves claim expiry, backoff and lease expiry
/// together — which is what makes a timing test possible without a wall-clock wait.</summary>
public sealed class FakeClock : IClock
{
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The current instant, always with a zero offset.</summary>
    public DateTimeOffset UtcNow => _now;

    /// <summary>Moves the clock forward.</summary>
    /// <param name="by">How far to move. Must not be negative.</param>
    public void Advance(TimeSpan by)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(by, TimeSpan.Zero);
        _now = _now.Add(by);
    }

    /// <summary>Moves the clock to an instant.</summary>
    /// <param name="instant">The instant, which is converted to UTC.</param>
    public void SetTo(DateTimeOffset instant) => _now = instant.ToUniversalTime();
}

/// <summary>A tenant a test sets. In D3 the only real value is the implicit tenant, so this exists
/// for the tests that prove a value is read from the ambient context rather than assumed.</summary>
public sealed class FakeCurrentTenant : ICurrentTenant
{
    /// <summary>The tenant every read returns.</summary>
    public TenantId Current { get; set; } = TenantId.Implicit;
}

/// <summary>A principal a test sets. <see cref="Abstractions.Principal.Anonymous"/> is the ordinary
/// value — the total principal is never null.</summary>
public sealed class FakeCurrentPrincipal : ICurrentPrincipal
{
    /// <summary>The principal every read returns.</summary>
    public Principal Current { get; set; } = Principal.Anonymous;
}

/// <summary>A principal of each kind, for a test that needs one it did not have to authenticate.
/// Framework-only: no fake account, membership or user directory of any kind, which is a module's
/// own knowledge and never a framework fake's.</summary>
public static class FakePrincipals
{
    /// <summary>No credential was presented.</summary>
    public static Principal Anonymous => Principal.Anonymous;

    /// <summary>Platform itself, acting on its own behalf.</summary>
    public static Principal System => Principal.LocalSystem;

    /// <summary>A principal Identity authenticated from a credential.</summary>
    /// <param name="issuer">The asserting boundary.</param>
    /// <param name="subject">The subject within that boundary.</param>
    public static Principal Account(string issuer = "test-issuer", string subject = "test-subject") =>
        new(new PrincipalId(issuer, subject), PrincipalKind.Account, subject, null);

    /// <summary>A principal established from an upstream-proxy assertion, with no account behind
    /// it.</summary>
    /// <param name="issuer">The asserting boundary.</param>
    /// <param name="subject">The subject within that boundary.</param>
    public static Principal Delegated(string issuer = "test-issuer", string subject = "test-subject") =>
        new(new PrincipalId(issuer, subject), PrincipalKind.Delegated, subject, null);
}

/// <summary>An authentication provider that presents no credential, so every request observes
/// <see cref="Abstractions.Principal.Anonymous"/>. Its purpose is to exist: an
/// <see cref="CompositionProfile.Operated"/> host with no provider registered refuses to start
/// (I-C1), and a test host is still a host. A test that wants an authenticated principal sets
/// <see cref="FakeCurrentPrincipal"/> rather than authenticating through this.</summary>
public sealed class FakeAuthenticationProvider : IAuthenticationProvider
{
    /// <inheritdoc/>
    public string Name => "Platform.Testing.Authentication";

    /// <inheritdoc/>
    public Task<Result<Principal, AuthenticationError>> AuthenticateAsync(
        IAuthenticationRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(Result<Principal, AuthenticationError>.Success(Principal.Anonymous));
}

/// <summary>An audit sink that declares durability and keeps what it was given in memory, so an
/// <see cref="CompositionProfile.Operated"/> test host satisfies I-C2 without the audit store module
/// being present. Declaring <see cref="IsDurable"/> is a statement about the sink, and this one is
/// honest for the lifetime of the process it runs in — which is the whole lifetime a test has.</summary>
/// <remarks>Reads only. There is deliberately no clear and no write beyond the sink's own contract:
/// a test helper that can delete an audit row is a test helper that can be used to prove the wrong
/// thing.</remarks>
public sealed class FakeDurableAuditSink : IAuditSink
{
    private readonly List<AuditEvent> _written = [];
    private readonly Lock _gate = new();

    /// <inheritdoc/>
    public string Name => "Platform.Testing.DurableAudit";

    /// <inheritdoc/>
    public bool IsDurable => true;

    /// <summary>Everything written, in the order it arrived.</summary>
    public IReadOnlyList<AuditEvent> Written
    {
        get { lock (_gate) { return _written.ToList(); } }
    }

    /// <inheritdoc/>
    public Task<Result<AuditError>> WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _written.Add(auditEvent);
        }

        return Task.FromResult(Result<AuditError>.Success());
    }
}

/// <summary>A culture a test sets. <see cref="CultureTag.Invariant"/> is the ordinary value: nothing
/// resolves one in D3.</summary>
public sealed class FakeCurrentCulture : ICurrentCulture
{
    /// <summary>The culture every read returns.</summary>
    public CultureTag Current { get; set; } = CultureTag.Invariant;
}
