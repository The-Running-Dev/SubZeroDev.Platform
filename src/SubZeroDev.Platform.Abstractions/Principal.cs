using System.Security.Claims;

namespace SubZeroDev.Platform.Abstractions;

/// <summary>The pair that identifies a principal. Both halves opaque, compared ordinally, never
/// parsed, normalised or case-folded by Platform — the moment Platform knows what a subject looks
/// like it has an opinion about which providers are legal.</summary>
/// <param name="Issuer">The asserting boundary. Non-empty.</param>
/// <param name="Subject">The subject within that boundary. Non-empty.</param>
public readonly record struct PrincipalId(string Issuer, string Subject)
{
    /// <summary>The asserting boundary. Non-empty. Never trimmed, normalised or case-folded — that
    /// is the whole of the constraint either half carries.</summary>
    public string Issuer { get; } = RequireNonEmpty(Issuer, nameof(Issuer));

    /// <summary>The subject within that boundary. Non-empty. Never trimmed, normalised or
    /// case-folded.</summary>
    public string Subject { get; } = RequireNonEmpty(Subject, nameof(Subject));

    /// <summary>The well-known id of no principal at all. Distinct from <see cref="LocalSystem"/>.</summary>
    public static PrincipalId Anonymous { get; } = new("platform", "anonymous");

    /// <summary>The well-known id Platform itself acts under when nobody signed in for the work.
    /// Renders <c>system:local</c>.</summary>
    public static PrincipalId LocalSystem { get; } = new("system", "local");

    /// <summary>A display and trace form only. Not injective across every possible issuer and
    /// subject, so it must never be split to recover the pair — anywhere the pair must survive
    /// storage it is stored as two columns.</summary>
    public override string ToString() => $"{Issuer}:{Subject}";

    private static string RequireNonEmpty(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrEmpty(value, parameterName);
        return value;
    }
}

/// <summary>What established the principal, and therefore whether the actor is resolvable
/// afterwards.</summary>
public enum PrincipalKind
{
    /// <summary>No credential was presented.</summary>
    Anonymous,

    /// <summary>Established from a credential Identity authenticated.</summary>
    Account,

    /// <summary>Established from an upstream-proxy assertion, with no account behind it.</summary>
    Delegated,

    /// <summary>Platform itself, acting on its own behalf.</summary>
    System,
}

/// <summary>The ambient actor. Total: there is no null principal, exactly as
/// <see cref="TenantId.Implicit"/> is a tenant rather than the absence of one. Derived per request,
/// never persisted — only <see cref="PrincipalId"/> reaches storage.</summary>
/// <param name="Id">The principal's identity.</param>
/// <param name="Kind">What established the principal.</param>
/// <param name="DisplayName">A human-readable label, when one is available.</param>
/// <param name="Claims">The raw authentication result. Null for <see cref="PrincipalKind.Anonymous"/>,
/// for <see cref="PrincipalKind.System"/>, and for any <see cref="PrincipalKind.Delegated"/> principal
/// whose asserting boundary produced no claims. No Platform decision may read it — authorization
/// reads permissions, not claims.</param>
public sealed record Principal(
    PrincipalId Id,
    PrincipalKind Kind,
    string? DisplayName,
    ClaimsPrincipal? Claims)
{
    /// <summary>No credential was presented.</summary>
    public static Principal Anonymous { get; } = new(PrincipalId.Anonymous, PrincipalKind.Anonymous, null, null);

    /// <summary>Platform itself, acting on its own behalf — the actor a request the host itself
    /// originates observes, with no authentication anywhere in the composition.</summary>
    public static Principal LocalSystem { get; } = new(PrincipalId.LocalSystem, PrincipalKind.System, "Local System", null);
}
