namespace SubZeroDev.Platform.SignIn;

/// <summary>What the authorize-request hook is given: the sign-in method, the issuer's authorize
/// address and the parameters the module has built so far.</summary>
/// <param name="Method">The sign-in method's name, as configured.</param>
/// <param name="Address">The issuer's authorize address, before parameters are appended.</param>
/// <param name="Parameters">The parameters the module has assembled: its own, then the method's
/// <c>ExtraAuthorizeParameters</c>.</param>
public sealed record AuthorizeRequestContext(string Method, Uri Address, IReadOnlyDictionary<string, string> Parameters);

/// <summary>What the end-session hook is given: the sign-in method and the address the template or
/// the issuer's discovery document produced.</summary>
/// <param name="Method">The sign-in method's name, as configured.</param>
/// <param name="Address">The end-session address the module would otherwise send the person to.</param>
public sealed record EndSessionContext(string Method, Uri Address);

/// <summary>A host's code for a provider that departs from the standard in a way two configuration
/// settings cannot express. Reaching for it is expected for some providers. It runs above
/// configuration, after it has bound, and cannot change which issuer, key source, audience or
/// algorithm Platform trusts: it is handed no part of the generic bearer path's settings (I-S3).</summary>
public sealed class SignInHooks
{
    /// <summary>Called when the module builds the authorize address. Returns the parameter set to
    /// send. The module restores <c>client_id</c>, <c>redirect_uri</c>, <c>response_type</c>,
    /// <c>scope</c>, <c>state</c>, <c>code_challenge</c>, <c>code_challenge_method</c> and
    /// <c>nonce</c> to its own values afterwards, so a hook adds or changes any other parameter and
    /// cannot weaken the flow.</summary>
    public Func<AuthorizeRequestContext, ValueTask<IReadOnlyDictionary<string, string>>>? AdjustAuthorizeRequest { get; set; }

    /// <summary>Called after the template or the discovery document has produced the end-session
    /// address, or, when neither exists, with the address of the post-sign-out page. Returns the
    /// address to send the person to. It must be an absolute http or https address.</summary>
    public Func<EndSessionContext, ValueTask<Uri>>? ReplaceEndSessionAddress { get; set; }
}
