using System.Security.Claims;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using SubZeroDev.Platform.Abstractions;

namespace SubZeroDev.Platform.Identity;

/// <summary>The generic bearer path's provider: one trusted issuer or issuer pattern, validated with asymmetric keys
/// held from startup. Built from <see cref="BearerSettings"/>, never constructed by a consumer
/// (<c>20-contract.md</c> § 12), so no <c>Microsoft.IdentityModel</c> type crosses a public
/// surface (I-I12).</summary>
/// <remarks>Never fetches key material while authenticating (I-I8): keys are fixed at construction or
/// read from a cache that only startup and the refresh work item fill.</remarks>
internal sealed class ConfiguredBearerAuthenticationProvider : IAuthenticationProvider, ISecondCredentialProvider
{
    private const string AuthorizationHeader = "Authorization";
    private const string BearerPrefix = "Bearer ";

    private readonly BearerSettings _settings;
    private readonly JsonWebTokenHandler _handler;
    private readonly TokenValidationParameters _parameters;
    private readonly KeySetCache? _cache;

    internal ConfiguredBearerAuthenticationProvider(
        BearerSettings settings, JsonWebTokenHandler? handler = null, KeySetCache? cache = null)
    {
        _settings = settings;
        _cache = cache;
        _handler = handler ?? new JsonWebTokenHandler();

        _parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            IssuerValidator = (issuer, _, _) => settings.Claims(issuer)
                ? issuer
                : throw new SecurityTokenInvalidIssuerException("the issuer is not one this provider trusts"),
            ValidateAudience = true,
            ValidAudiences = settings.Audiences,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            ClockSkew = settings.ClockTolerance,
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,
            ValidAlgorithms = settings.Algorithms,
            // A token naming a kid is checked against that kid's keys only; one naming none is tried
            // against every held key. Never "any key will do" for a kid that matches nothing.
            TryAllIssuerSigningKeys = false,
            IssuerSigningKeyResolver = (_, _, kid, _) =>
            {
                var keys = (settings.SigningKeys ?? _cache?.Current)?.GetSigningKeys() ?? [];
                return string.IsNullOrEmpty(kid) ? keys : keys.Where(key => key.KeyId == kid).ToList();
            },
        };
    }

    /// <inheritdoc/>
    public string Name => _settings.Name;

    /// <inheritdoc/>
    public async Task<Result<Principal, AuthenticationError>> AuthenticateAsync(
        IAuthenticationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!TryReadBearerToken(request, out var token))
        {
            return Result<Principal, AuthenticationError>.Success(Principal.Anonymous);
        }

        var (validated, _) = await ValidateAsync(token).ConfigureAwait(false);
        return validated;
    }

    /// <inheritdoc/>
    async Task<SecondCredentialValidation> ISecondCredentialProvider.ValidateSecondCredentialAsync(
        string token, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);

        var (validated, parsed) = await ValidateAsync(token).ConfigureAwait(false);
        var issuedAt = validated.IsSuccess ? ReadIssuedAt(parsed!) : null;
        return SecondCredential.Check(validated, issuedAt, now, _settings.ClockTolerance);
    }

    /// <summary>Validates a token and, once it is read, answers it alongside the result.</summary>
    private async Task<(Result<Principal, AuthenticationError> Result, JsonWebToken? Token)> ValidateAsync(string token)
    {
        JsonWebToken parsed;
        try
        {
            parsed = _handler.ReadJsonWebToken(token);
        }
        catch (Exception ex) when (ex is SecurityTokenException or ArgumentException or JsonException)
        {
            return (Rejected(), null);
        }

        if (string.IsNullOrEmpty(parsed.Issuer))
        {
            return (Rejected(), parsed);
        }

        if (!_settings.Claims(parsed.Issuer))
        {
            return (Result<Principal, AuthenticationError>.Failure(AuthenticationError.CredentialNotClaimed(Name)), parsed);
        }

        if (_settings.SigningKeys is null && _cache?.Current is null)
        {
            return (Result<Principal, AuthenticationError>.Failure(AuthenticationError.KeyMaterialUnavailable(Name)), parsed);
        }

        TokenValidationResult validated;
        try
        {
            validated = await _handler.ValidateTokenAsync(parsed, _parameters).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (Failed(), parsed);
        }

        if (!validated.IsValid)
        {
            // A fault in the library itself, as opposed to a defect in the token's own content.
            return (validated.Exception is null or SecurityTokenException ? Rejected() : Failed(), parsed);
        }

        if (!TryReadPayloadString(parsed, _settings.SubjectClaim, out var subject) || subject.Length == 0)
        {
            return (Rejected(), parsed);
        }

        string? displayName = null;
        if (_settings.DisplayNameClaim is { } displayNameClaim
            && TryReadPayloadString(parsed, displayNameClaim, out var display)
            && display.Length > 0)
        {
            displayName = display;
        }

        var identity = new ClaimsIdentity(validated.ClaimsIdentity.Claims, authenticationType: Name);

        return (Result<Principal, AuthenticationError>.Success(new Principal(
            new PrincipalId(parsed.Issuer, subject),
            PrincipalKind.Account,
            displayName,
            new ClaimsPrincipal(identity))), parsed);
    }

    private Result<Principal, AuthenticationError> Rejected() =>
        Result<Principal, AuthenticationError>.Failure(AuthenticationError.CredentialRejected(Name));

    private Result<Principal, AuthenticationError> Failed() =>
        Result<Principal, AuthenticationError>.Failure(AuthenticationError.ProviderFailed(Name));

    /// <summary>Reads a claim as exactly the string the token carries; any other JSON kind is
    /// not a string.</summary>
    private static bool TryReadPayloadString(JsonWebToken token, string claim, out string value)
    {
        value = "";
        try
        {
            using var payload = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(token.EncodedPayload));
            if (payload.RootElement.ValueKind == JsonValueKind.Object
                && payload.RootElement.TryGetProperty(claim, out var element)
                && element.ValueKind == JsonValueKind.String)
            {
                value = element.GetString() ?? "";
                return true;
            }
        }
        catch (JsonException)
        {
        }

        return false;
    }

    /// <summary>Reads <c>iat</c> as a whole number of seconds since the epoch; absent, or any other
    /// JSON kind, is no issued-at.</summary>
    private static DateTimeOffset? ReadIssuedAt(JsonWebToken token)
    {
        try
        {
            using var payload = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(token.EncodedPayload));
            return payload.RootElement.ValueKind == JsonValueKind.Object
                && payload.RootElement.TryGetProperty("iat", out var element)
                && element.ValueKind == JsonValueKind.Number
                && element.TryGetInt64(out var seconds)
                    ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                    : null;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static bool TryReadBearerToken(IAuthenticationRequest request, out string token)
    {
        token = "";

        if (!request.Headers.TryGetValue(AuthorizationHeader, out var values) || values.Count == 0)
        {
            return false;
        }

        var value = values[0];
        if (!value.StartsWith(BearerPrefix, StringComparison.Ordinal) || value.Length == BearerPrefix.Length)
        {
            return false;
        }

        token = value[BearerPrefix.Length..];
        return true;
    }
}
