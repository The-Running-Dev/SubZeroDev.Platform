namespace SubZeroDev.Platform.Abstractions;

/// <summary>Which shape the host claims to be. Configuration, and fingerprinted.</summary>
public enum CompositionProfile
{
    /// <summary>Identity-free, billing-free, licence-free. No commercial package is present.</summary>
    Local,

    /// <summary>Authenticated at the transport, with a durable audit sink.</summary>
    Operated,
}
