using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SubZeroDev.Platform.GameEdge.Tests.Support;

/// <summary>The one bearer issuer every terminate-mode test class pins. It is pinned through the
/// process environment, which is shared, so a second issuer in a second class would overwrite the first
/// under parallel test classes; one owner removes the race. The factory hands its settings to the entry
/// point flattened, with every parent key present as an empty value, and Identity refuses a provider
/// section that carries a value — environment variables reach the configuration without that
/// flattening. Only a terminate-mode host composes Identity, so no other class reads them.</summary>
internal static class TestIssuer
{
    public const string Issuer = "https://issuer.test";
    public const string Audience = "game.test";
    public const string KeyId = "issuer-key-1";

    /// <summary>The issuer's signing key. Reading it pins the issuer, because the static constructor
    /// runs first.</summary>
    public static ECDsa Key { get; }

    static TestIssuer()
    {
        Key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var parameters = Key.ExportParameters(includePrivateParameters: false);
        var jwks = JsonSerializer.Serialize(new
        {
            keys = new[]
            {
                new
                {
                    kty = "EC",
                    crv = "P-256",
                    x = Base64Url.EncodeToString(parameters.Q.X),
                    y = Base64Url.EncodeToString(parameters.Q.Y),
                    kid = KeyId,
                    use = "sig",
                    alg = "ES256",
                },
            },
        });

        Environment.SetEnvironmentVariable("Platform__Identity__Bearer__game__Issuer", Issuer);
        Environment.SetEnvironmentVariable("Platform__Identity__Bearer__game__SigningKeys", jwks);
        Environment.SetEnvironmentVariable("Platform__Identity__Bearer__game__Audiences__0", Audience);
        Environment.SetEnvironmentVariable("Platform__Identity__Bearer__game__Algorithms__0", "ES256");
    }

    /// <summary>Pins the issuer. A class that starts a terminate-mode host calls it before the first
    /// host starts, even if no test of the class makes a token.</summary>
    public static void Pin()
    {
    }

    /// <summary>A token for <paramref name="subject"/> that the pinned issuer's keys accept, unless
    /// <paramref name="key"/> or <paramref name="issuer"/> says otherwise.</summary>
    public static string Token(string subject, ECDsa? key = null, string issuer = Issuer, long issuedSecondsAgo = 0)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - issuedSecondsAgo;
        var header = Encode(new { alg = "ES256", kid = KeyId, typ = "JWT" });
        var payload = Encode(new { iss = issuer, sub = subject, aud = Audience, iat = now, nbf = now, exp = now + 300 });
        var input = header + "." + payload;
        return input + "." + Base64Url.EncodeToString((key ?? Key).SignData(Encoding.ASCII.GetBytes(input), HashAlgorithmName.SHA256));
    }

    private static string Encode(object value) => Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(value));
}
