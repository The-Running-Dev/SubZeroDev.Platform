using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Hosting;

namespace SubZeroDev.Platform.SignIn;

/// <summary>The state a begin stores for its callback: short-lived, signed and encrypted.</summary>
internal sealed record BeginState(string State, string Verifier, string Nonce, DateTimeOffset ExpiresAt);

/// <summary>The whole session (I-S1): issuer, subject, access token and its expiry, and nothing else.</summary>
internal sealed record SessionState(string Issuer, string Subject, string AccessToken, DateTimeOffset ExpiresAt);

/// <summary>The four endpoints of one sign-in method. Every one is exempt from the endpoint-level
/// authorization requirement, because its caller is by definition not yet signed in (or, for the
/// token endpoint, is proving itself with the session cookie and the anti-forgery header).</summary>
internal static class SignInEndpoints
{
    /// <summary>The header a same-origin caller sends to the token endpoint. A foreign page cannot
    /// set a custom header without a cross-origin preflight this module never answers.</summary>
    internal const string AntiForgeryHeader = "X-Platform-SignIn";

    private const string SessionCookie = "subzerodev.signin.session.";
    private const string BeginCookie = "subzerodev.signin.begin.";
    private const string FailureText = "Sign-in failed.";
    private static readonly TimeSpan BeginLifetime = TimeSpan.FromMinutes(10);

    internal static void Map(IEndpointRouteBuilder endpoints, SignInSettings method)
    {
        const string reason =
            "The sign-in module's endpoints run before there is a signed-in person to authorize; the "
            + "session is the module's own cookie and never an input to the authentication seam (design/20-contract.md § 13).";

        var group = endpoints.MapGroup($"/signin/{method.Name}");
        group.MapGet("begin", Handler(context => BeginAsync(context, method))).ExemptFromPlatformAuthorization(reason);
        endpoints.MapGet(method.RedirectPath, Handler(context => CallbackAsync(context, method))).ExemptFromPlatformAuthorization(reason);
        group.MapPost("token", Handler(context => Task.FromResult(Token(context, method)))).ExemptFromPlatformAuthorization(reason);
        group.MapPost("signout", Handler(context => SignOutAsync(context, method))).ExemptFromPlatformAuthorization(reason);
    }

    // A delegate typed as Func<HttpContext, Task<IResult>> rather than a bare lambda, so the router
    // writes the returned result instead of treating the lambda as a RequestDelegate that discards it.
    private static Delegate Handler(Func<HttpContext, Task<IResult>> handler) => handler;

    private static async Task<IResult> BeginAsync(HttpContext context, SignInSettings method)
    {
        var services = context.RequestServices;
        var runtime = services.GetRequiredService<SignInRuntime>();
        var now = services.GetRequiredService<IClock>().UtcNow;

        var metadata = await runtime.Metadata
            .GetAsync(method, services.GetRequiredService<SignInTransport>(), now, context.RequestAborted)
            .ConfigureAwait(false);
        if (metadata is null)
        {
            Log(context, method, "the issuer's discovery document could not be read");
            return Failure(StatusCodes.Status502BadGateway);
        }

        var state = RandomToken();
        var verifier = RandomToken();
        var nonce = RandomToken();
        var challenge = IssuerClient.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        // The module's own parameters are set last, so nothing configured or returned by a hook can
        // replace them, and the extras and the hook's additions sit around them.
        var owned = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["response_type"] = "code",
            ["client_id"] = method.ClientId,
            ["redirect_uri"] = RedirectUri(context, method),
            ["scope"] = method.Scopes,
            ["state"] = state,
            ["nonce"] = nonce,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
        };

        var parameters = new Dictionary<string, string>(method.ExtraAuthorizeParameters, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in owned)
        {
            parameters[pair.Key] = pair.Value;
        }

        var hook = runtime.Hooks.Find(method.Name)?.AdjustAuthorizeRequest;
        if (hook is not null)
        {
            var adjusted = await hook(new AuthorizeRequestContext(
                method.Name, metadata.AuthorizationEndpoint, new Dictionary<string, string>(parameters))).ConfigureAwait(false);

            parameters = new Dictionary<string, string>(adjusted, StringComparer.OrdinalIgnoreCase);
            foreach (var pair in owned)
            {
                parameters[pair.Key] = pair.Value;
            }
        }

        var address = AppendQuery(metadata.AuthorizationEndpoint, parameters);

        var begin = new BeginState(state, verifier, nonce, now + BeginLifetime);
        context.Response.Cookies.Append(
            BeginCookie + method.Name,
            Protector(services, "Begin", method).Protect(JsonSerializer.Serialize(begin)),
            CookieOptions(context, BeginLifetime));
        NoStore(context);

        return Results.Redirect(address.AbsoluteUri);
    }

    private static async Task<IResult> CallbackAsync(HttpContext context, SignInSettings method)
    {
        var services = context.RequestServices;
        var runtime = services.GetRequiredService<SignInRuntime>();
        var now = services.GetRequiredService<IClock>().UtcNow;
        NoStore(context);

        var cookieName = BeginCookie + method.Name;
        var stored = ReadBegin(context, method, cookieName, now);

        // The begin cookie is single-use whatever happens next.
        context.Response.Cookies.Delete(cookieName, CookieOptions(context, TimeSpan.Zero));

        // I-S1: no session unless the callback's state equals the state this begin stored.
        var returned = context.Request.Query["state"].ToString();
        var code = context.Request.Query["code"].ToString();
        if (stored is null
            || context.Request.Query.ContainsKey("error")
            || returned.Length == 0
            || code.Length == 0
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(returned), Encoding.UTF8.GetBytes(stored.State)))
        {
            Log(context, method, "the callback carried no matching state");
            return Failure(StatusCodes.Status400BadRequest);
        }

        var metadata = await runtime.Metadata
            .GetAsync(method, services.GetRequiredService<SignInTransport>(), now, context.RequestAborted)
            .ConfigureAwait(false);
        if (metadata is null)
        {
            Log(context, method, "the issuer's discovery document could not be read");
            return Failure(StatusCodes.Status502BadGateway);
        }

        var tokens = await IssuerClient.ExchangeAsync(
            method, metadata, services.GetRequiredService<SignInTransport>(), code, RedirectUri(context, method), stored.Verifier, context.RequestAborted)
            .ConfigureAwait(false);
        var subject = tokens is null ? null : IssuerClient.SubjectOf(tokens.IdToken, method, stored.Nonce, now);
        if (tokens is null || subject is null)
        {
            Log(context, method, "the code exchange was refused or its id token did not check");
            return Failure(StatusCodes.Status502BadGateway);
        }

        var lifetime = TimeSpan.FromSeconds(Math.Clamp(tokens.ExpiresInSeconds, 1, 24 * 60 * 60));
        var session = new SessionState(method.Issuer, subject, tokens.AccessToken, now + lifetime);
        context.Response.Cookies.Append(
            SessionCookie + method.Name,
            Protector(services, "Session", method).Protect(JsonSerializer.Serialize(session)),
            CookieOptions(context, lifetime));

        return Results.Redirect(Absolute(context, "/").AbsoluteUri);
    }

    private static IResult Token(HttpContext context, SignInSettings method)
    {
        NoStore(context);
        if (string.IsNullOrWhiteSpace(context.Request.Headers[AntiForgeryHeader].ToString()))
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        var now = context.RequestServices.GetRequiredService<IClock>().UtcNow;
        var session = ReadSession(context, method, now);
        if (session is null)
        {
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        }

        return Results.Json(new
        {
            accessToken = session.AccessToken,
            expiresAt = session.ExpiresAt.ToUnixTimeSeconds(),
        });
    }

    private static async Task<IResult> SignOutAsync(HttpContext context, SignInSettings method)
    {
        var services = context.RequestServices;
        var runtime = services.GetRequiredService<SignInRuntime>();
        var now = services.GetRequiredService<IClock>().UtcNow;
        NoStore(context);

        // I-S2: this clears the module's own session. It revokes nothing at the issuer or at
        // Platform; an access token already issued stays valid until it expires.
        context.Response.Cookies.Delete(SessionCookie + method.Name, CookieOptions(context, TimeSpan.Zero));

        var returnTo = Absolute(context, method.PostSignOutPath);
        Uri address;
        if (method.EndSessionTemplate is not null)
        {
            address = SignInSettings.ExpandTemplate(method.EndSessionTemplate, method.ClientId, returnTo);
        }
        else
        {
            var metadata = await runtime.Metadata
                .GetAsync(method, services.GetRequiredService<SignInTransport>(), now, context.RequestAborted)
                .ConfigureAwait(false);
            address = metadata?.EndSessionEndpoint is { } discovered
                ? AppendQuery(discovered, new Dictionary<string, string>
                {
                    ["client_id"] = method.ClientId,
                    ["post_logout_redirect_uri"] = returnTo.AbsoluteUri,
                })
                : returnTo;
        }

        var hook = runtime.Hooks.Find(method.Name)?.ReplaceEndSessionAddress;
        if (hook is not null)
        {
            address = await hook(new EndSessionContext(method.Name, address)).ConfigureAwait(false);
            if (!address.IsAbsoluteUri || address.Scheme is not ("https" or "http"))
            {
                throw new InvalidOperationException(
                    $"The ReplaceEndSessionAddress hook for sign-in method '{method.Name}' returned an address that is not an absolute http or https address.");
            }
        }

        // 303, so the browser follows the redirect with a GET whatever method signed out.
        context.Response.Headers.Location = address.AbsoluteUri;
        return Results.StatusCode(StatusCodes.Status303SeeOther);
    }

    private static BeginState? ReadBegin(HttpContext context, SignInSettings method, string cookieName, DateTimeOffset now)
    {
        var raw = context.Request.Cookies[cookieName];
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        try
        {
            var state = JsonSerializer.Deserialize<BeginState>(Protector(context.RequestServices, "Begin", method).Unprotect(raw));
            return state is not null && state.ExpiresAt > now ? state : null;
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException)
        {
            // Keys that differ between instances read as a missing cookie (design/10-design.md, failure modes).
            return null;
        }
    }

    private static SessionState? ReadSession(HttpContext context, SignInSettings method, DateTimeOffset now)
    {
        var raw = context.Request.Cookies[SessionCookie + method.Name];
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        try
        {
            var session = JsonSerializer.Deserialize<SessionState>(Protector(context.RequestServices, "Session", method).Unprotect(raw));
            return session is not null
                && session.ExpiresAt > now
                && string.Equals(session.Issuer, method.Issuer, StringComparison.Ordinal)
                    ? session
                    : null;
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException)
        {
            return null;
        }
    }

    private static IDataProtector Protector(IServiceProvider services, string purpose, SignInSettings method) =>
        services.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("SubZeroDev.Platform.SignIn", purpose, method.Name);

    private static CookieOptions CookieOptions(HttpContext context, TimeSpan lifetime) => new()
    {
        HttpOnly = true,
        Secure = context.Request.IsHttps,

        // The callback is a top-level GET navigation from the issuer, which Lax sends the cookie on.
        SameSite = SameSiteMode.Lax,
        IsEssential = true,
        Path = "/",
        MaxAge = lifetime == TimeSpan.Zero ? null : lifetime,
    };

    private static string RedirectUri(HttpContext context, SignInSettings method) =>
        Absolute(context, method.RedirectPath).AbsoluteUri;

    private static Uri Absolute(HttpContext context, string path) =>
        new($"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}{path}", UriKind.Absolute);

    private static Uri AppendQuery(Uri address, IReadOnlyDictionary<string, string> parameters)
    {
        var query = string.Join(
            '&',
            parameters.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        var builder = new UriBuilder(address);
        builder.Query = string.IsNullOrEmpty(address.Query)
            ? query
            : address.Query.TrimStart('?') + "&" + query;
        return builder.Uri;
    }

    private static string RandomToken() => IssuerClient.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    private static void NoStore(HttpContext context) => context.Response.Headers.CacheControl = "no-store";

    // The person sees one sentence whichever half failed; the log names the method and the cause,
    // never a token, a code or a state.
    private static IResult Failure(int status) => Results.Text(FailureText, "text/plain", Encoding.UTF8, status);

    private static void Log(HttpContext context, SignInSettings method, string cause) =>
        context.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("SubZeroDev.Platform.SignIn")
            .LogWarning(
                "Sign-in method {Method} for issuer {Issuer} failed: {Cause}.",
                method.Name,
                method.Issuer,
                cause);
}
