using System.Net.Http.Headers;
using System.Text.Json;

namespace SubZeroDev.Platform.SignIn;

/// <summary>Where an outbound request to the issuer gets its connection from. A seam so a test
/// observes every request; the default is a plain handler that follows no redirect.</summary>
internal sealed class SignInTransport(Func<HttpMessageHandler> create)
{
    internal static SignInTransport Default { get; } =
        new(() => new SocketsHttpHandler { AllowAutoRedirect = false });

    internal HttpMessageHandler Create() => create();
}

/// <summary>The three addresses the module needs from an issuer's discovery document.</summary>
internal sealed record IssuerMetadata(Uri AuthorizationEndpoint, Uri TokenEndpoint, Uri? EndSessionEndpoint);

/// <summary>What the token endpoint returned, reduced to what the session is built from.</summary>
internal sealed record TokenResponse(string AccessToken, int ExpiresInSeconds, string IdToken);

/// <summary>Discovery documents, fetched on first use and held for a bounded time. Never fetched at
/// startup: a sign-in method must not make an unreachable issuer a reason a host will not start.</summary>
internal sealed class IssuerMetadataCache
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, (IssuerMetadata Metadata, DateTimeOffset FetchedAt)> _cache = new(StringComparer.Ordinal);

    internal async Task<IssuerMetadata?> GetAsync(
        SignInSettings settings,
        SignInTransport transport,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cache.TryGetValue(settings.Issuer, out var held) && now - held.FetchedAt < Lifetime)
            {
                return held.Metadata;
            }

            var fetched = await IssuerClient.FetchMetadataAsync(settings, transport, cancellationToken).ConfigureAwait(false);
            if (fetched is not null)
            {
                _cache[settings.Issuer] = (fetched, now);
            }

            return fetched;
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>The module's outbound calls to an issuer. Bounded in time and size; a failure is a
/// <see langword="null"/> result, because the caller's answer to every failure is the same generic
/// sign-in error and the module retries nothing.</summary>
internal static class IssuerClient
{
    /// <summary>The bound on one outbound call. Fixed, not a setting.</summary>
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private const int MaximumResponseBytes = 1024 * 1024;

    internal static async Task<IssuerMetadata?> FetchMetadataAsync(
        SignInSettings settings,
        SignInTransport transport,
        CancellationToken cancellationToken)
    {
        try
        {
            using var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            bound.CancelAfter(Timeout);

            using var client = NewClient(transport);
            var address = settings.Issuer.TrimEnd('/') + "/.well-known/openid-configuration";
            var text = await client.GetStringAsync(address, bound.Token).ConfigureAwait(false);

            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;

            // OpenID Connect Discovery 4.3: the document's issuer must be the one it was fetched for.
            if (!string.Equals(Text(root, "issuer"), settings.Issuer, StringComparison.Ordinal))
            {
                return null;
            }

            var authorize = Endpoint(root, "authorization_endpoint");
            var token = Endpoint(root, "token_endpoint");
            if (authorize is null || token is null)
            {
                return null;
            }

            return new IssuerMetadata(authorize, token, Endpoint(root, "end_session_endpoint"));
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or OperationCanceledException or InvalidOperationException)
        {
            return null;
        }
    }

    internal static async Task<TokenResponse?> ExchangeAsync(
        SignInSettings settings,
        IssuerMetadata metadata,
        SignInTransport transport,
        string code,
        string redirectUri,
        string verifier,
        CancellationToken cancellationToken)
    {
        try
        {
            using var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            bound.CancelAfter(Timeout);

            using var client = NewClient(transport);
            using var request = new HttpRequestMessage(HttpMethod.Post, metadata.TokenEndpoint)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code",
                    ["code"] = code,
                    ["redirect_uri"] = redirectUri,
                    ["client_id"] = settings.ClientId,
                    ["code_verifier"] = verifier,
                }),
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await client.SendAsync(request, bound.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var text = await response.Content.ReadAsStringAsync(bound.Token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;

            var accessToken = Text(root, "access_token");
            var idToken = Text(root, "id_token");
            if (string.IsNullOrEmpty(accessToken) || string.IsNullOrEmpty(idToken))
            {
                return null;
            }

            var expiresIn = root.TryGetProperty("expires_in", out var expires)
                && expires.ValueKind == JsonValueKind.Number
                && expires.TryGetInt32(out var seconds)
                ? seconds
                : 3600;

            return new TokenResponse(accessToken, expiresIn, idToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or OperationCanceledException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Reads the claims of an id token received directly from the token endpoint, without
    /// checking its signature, as OpenID Connect Core 3.1.3.7 permits for a token that arrived over
    /// the exchange itself. Checks issuer, audience, nonce and expiry; returns the subject, or
    /// <see langword="null"/> when any check fails.</summary>
    internal static string? SubjectOf(string idToken, SignInSettings settings, string nonce, DateTimeOffset now)
    {
        var parts = idToken.Split('.');
        if (parts.Length != 3)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(Base64UrlDecode(parts[1]));
            var claims = document.RootElement;
            if (claims.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!string.Equals(Text(claims, "iss"), settings.Issuer, StringComparison.Ordinal)
                || !string.Equals(Text(claims, "nonce"), nonce, StringComparison.Ordinal)
                || !Audience(claims, settings.ClientId))
            {
                return null;
            }

            if (claims.TryGetProperty("exp", out var exp)
                && exp.ValueKind == JsonValueKind.Number
                && exp.TryGetInt64(out var expiry)
                && DateTimeOffset.FromUnixTimeSeconds(expiry) <= now)
            {
                return null;
            }

            var subject = Text(claims, "sub");
            return string.IsNullOrEmpty(subject) ? null : subject;
        }
        catch (Exception exception) when (exception is JsonException or FormatException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    internal static string Base64UrlEncode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');
        return Convert.FromBase64String(padded);
    }

    private static bool Audience(JsonElement claims, string clientId)
    {
        if (!claims.TryGetProperty("aud", out var aud))
        {
            return false;
        }

        return aud.ValueKind switch
        {
            JsonValueKind.String => string.Equals(aud.GetString(), clientId, StringComparison.Ordinal),
            JsonValueKind.Array => aud.EnumerateArray().Any(item =>
                item.ValueKind == JsonValueKind.String && string.Equals(item.GetString(), clientId, StringComparison.Ordinal)),
            _ => false,
        };
    }

    private static HttpClient NewClient(SignInTransport transport) =>
        new(transport.Create(), disposeHandler: true)
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
            MaxResponseContentBufferSize = MaximumResponseBytes,
        };

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static Uri? Endpoint(JsonElement root, string name)
    {
        var text = Text(root, name);
        if (text is null || !Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            return null;
        }

        // The same rule as the issuer itself: https, or http for a loopback host.
        return uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback) ? uri : null;
    }
}
