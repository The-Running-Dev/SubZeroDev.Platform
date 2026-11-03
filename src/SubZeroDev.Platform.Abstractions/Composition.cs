namespace SubZeroDev.Platform.Abstractions;

/// <summary>Which shape the host claims to be. Configuration, and fingerprinted.</summary>
public enum CompositionProfile
{
    /// <summary>Identity-free, billing-free, licence-free. No commercial package is present.</summary>
    Local,

    /// <summary>Authenticated at the transport, with a durable audit sink.</summary>
    Operated,

    /// <summary>Authenticates its callers and holds nothing about them: no Platform persistence, no
    /// durable audit sink, no tenant resolver, no paid entitlement and no permission grant. A host
    /// that forwards an authenticated identity to a workload, which authorizes and audits for
    /// itself.</summary>
    AuthenticatedTransport,
}
