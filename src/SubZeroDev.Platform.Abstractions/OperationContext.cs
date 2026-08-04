using System.Security.Claims;

namespace SubZeroDev.Platform.Abstractions;

/// <summary>The clock every persisted instant originates from, so a fake clock controls every
/// timestamp and no eligibility or expiry comparison reaches a database clock.</summary>
public interface IClock
{
    /// <summary>The current instant, always with a zero offset.</summary>
    DateTimeOffset UtcNow { get; }
}

/// <summary>One operation's ambient context: the five values every persisted row and every log line
/// is stamped from.</summary>
public interface IOperationScope : IDisposable
{
    /// <summary>The originating trace-id, unchanged through any depth of derived events.</summary>
    CorrelationId Correlation { get; }

    /// <summary>The tenant, always present.</summary>
    TenantId Tenant { get; }

    /// <summary>The principal, frequently absent — identity is D5.</summary>
    ClaimsPrincipal? Principal { get; }

    /// <summary>The trace context this scope established.</summary>
    TraceContext Trace { get; }

    /// <summary>The originating culture, unchanged through any depth of derived events. Defaults to
    /// <see cref="CultureTag.Invariant"/> — nothing resolves one in D3.</summary>
    CultureTag Culture { get; }
}

/// <summary>Opens an operation scope. There are two establishers — an inbound request, and each
/// dispatched message — so the write path has an owner rather than being invented twice.</summary>
public interface IOperationScopeFactory
{
    /// <summary>Origination: starts a real root trace and takes the correlation from it. The scope
    /// that calls this <em>is</em> the origin.</summary>
    /// <param name="tenant">The tenant for the scope.</param>
    /// <param name="principal">The principal, or <see langword="null"/>.</param>
    /// <param name="culture">The originating culture. Defaults to <see cref="CultureTag.Invariant"/>.</param>
    /// <returns>The scope, which restores the previous ambient context when disposed.</returns>
    IOperationScope Begin(TenantId tenant, ClaimsPrincipal? principal, CultureTag culture = default);

    /// <summary>Establishment from values the caller already holds — an adopted request context, or
    /// a dispatched message's new linked trace with the origin's correlation.</summary>
    /// <param name="established">The trace context the scope runs under.</param>
    /// <param name="correlation">The originating trace-id.</param>
    /// <param name="tenant">The tenant for the scope.</param>
    /// <param name="principal">The principal, or <see langword="null"/>.</param>
    /// <param name="culture">The originating culture. Defaults to <see cref="CultureTag.Invariant"/>.</param>
    /// <returns>The scope, which restores the previous ambient context when disposed.</returns>
    IOperationScope Begin(
        TraceContext established,
        CorrelationId correlation,
        TenantId tenant,
        ClaimsPrincipal? principal,
        CultureTag culture = default);
}

/// <summary>Reads the ambient scope. The only member here that can be null, and what makes
/// "there is no ambient scope" detectable.</summary>
public interface IOperationScopeAccessor
{
    /// <summary>The ambient scope, or <see langword="null"/> when none is open.</summary>
    IOperationScope? Current { get; }
}

/// <summary>The ambient tenant. In D3 always the implicit tenant — nothing resolves one from host,
/// header or claim.</summary>
public interface ICurrentTenant
{
    /// <summary>The ambient tenant.</summary>
    /// <exception cref="PlatformContractViolationException">No operation scope is open.</exception>
    TenantId Current { get; }
}

/// <summary>The ambient principal, which is frequently absent.</summary>
public interface ICurrentPrincipal
{
    /// <summary>The ambient principal, or <see langword="null"/>.</summary>
    /// <exception cref="PlatformContractViolationException">No operation scope is open.</exception>
    ClaimsPrincipal? Current { get; }
}

/// <summary>The ambient correlation. This is the value that does not change across outbox dispatch,
/// which is why it is a <see cref="CorrelationId"/> and not a <see cref="TraceContext"/>.</summary>
public interface ICurrentCorrelation
{
    /// <summary>The ambient correlation.</summary>
    /// <exception cref="PlatformContractViolationException">No operation scope is open.</exception>
    CorrelationId Current { get; }
}

/// <summary>The ambient culture. In D3 nothing resolves one — the interface exists so the outbox
/// column has a supplier, exactly as <see cref="ICurrentTenant"/> exists so the tenant column does.</summary>
public interface ICurrentCulture
{
    /// <summary>The ambient culture. Defaults to <see cref="CultureTag.Invariant"/>.</summary>
    /// <exception cref="PlatformContractViolationException">No operation scope is open.</exception>
    CultureTag Current { get; }
}
