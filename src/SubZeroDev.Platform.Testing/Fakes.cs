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

/// <summary>A culture a test sets. <see cref="CultureTag.Invariant"/> is the ordinary value: nothing
/// resolves one in D3.</summary>
public sealed class FakeCurrentCulture : ICurrentCulture
{
    /// <summary>The culture every read returns.</summary>
    public CultureTag Current { get; set; } = CultureTag.Invariant;
}
