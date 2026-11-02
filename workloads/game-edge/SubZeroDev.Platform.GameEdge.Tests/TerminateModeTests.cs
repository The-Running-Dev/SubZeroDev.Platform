using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SubZeroDev.Platform.GameEdge.Tests.Support;

namespace SubZeroDev.Platform.GameEdge.Tests;

/// <summary>G3 S3 — in terminate mode the edge authenticates the caller against the issuer's pinned
/// keys, refuses one with no credential, and hands the workload its own signed assertion in place of
/// the caller's credential.</summary>
public sealed class TerminateModeTests : IDisposable
{
    private const string TokenIssuer = "https://issuer.test";
    private const string TokenAudience = "game.test";
    private const string IssuerKeyId = "issuer-key-1";
    private const string HandoffIssuer = "https://edge.test/handoff";
    private const string HandoffAudience = "game-service.test";
    private const string HandoffKeyId = "edge-key-1";

    // One issuer for the class, pinned through the environment. The factory hands its settings to the
    // entry point flattened, with every parent key present as an empty value, and Identity refuses a
    // provider section that carries a value. Environment variables reach the configuration without
    // that flattening. Only a terminate-mode host composes Identity, so no other class reads them.
    private static readonly ECDsa IssuerKey;

    private readonly ECDsa handoffKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly string directory = Directory.CreateTempSubdirectory("game-edge-handoff-").FullName;
    private readonly string signingKeyPath;

    // A static constructor, so the issuer is pinned before the first host starts rather than at the
    // first token.
    static TerminateModeTests() => IssuerKey = PinIssuer();

    public TerminateModeTests()
    {
        signingKeyPath = Path.Combine(directory, "handoff.pem");
        File.WriteAllText(signingKeyPath, handoffKey.ExportPkcs8PrivateKeyPem());
    }

    public void Dispose()
    {
        handoffKey.Dispose();
        Directory.Delete(directory, recursive: true);
    }

    /// <summary>S3.3 — no <c>Authorization</c> is <c>401 CredentialRequired</c> from the edge, and the
    /// workload receives nothing.</summary>
    [Fact]
    public async Task S3_3_no_credential_is_refused_before_anything_is_forwarded()
    {
        await using var workload = await FakeWorkload.StartAsync();
        await using var edge = Terminate(workload.BaseAddress);
        using var client = edge.CreateClient();

        using var response = await client.PostAsync("/sessions", new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("CredentialRequired", body.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrEmpty(body.RootElement.GetProperty("correlation").GetString()));
        Assert.Empty(workload.Requests);
    }

    /// <summary>S3.4 and S3.5 — the workload receives no <c>Authorization</c> and exactly one
    /// assertion: the edge's, signed over this request for the token's issuer and subject. A
    /// caller-supplied assertion never arrives.</summary>
    [Fact]
    public async Task S3_4_and_S3_5_the_workload_receives_only_the_edges_assertion_for_the_tokens_principal()
    {
        await using var workload = await FakeWorkload.StartAsync();
        await using var edge = Terminate(workload.BaseAddress);
        using var client = edge.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/sessions/s-1/act?turn=2")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token("player-1"));
        request.Headers.TryAddWithoutValidation("SubZeroDev-Principal-Assertion", "caller.forged.value");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var forwarded = Assert.Single(workload.Requests);
        Assert.False(forwarded.Headers.ContainsKey("Authorization"));
        var assertion = Assert.Single(forwarded.Headers["SubZeroDev-Principal-Assertion"]);
        Assert.NotEqual("caller.forged.value", assertion);

        var (header, payload) = Verify(assertion);
        Assert.Equal("ES256", header.GetProperty("alg").GetString());
        Assert.Equal(HandoffKeyId, header.GetProperty("kid").GetString());
        Assert.Equal("szd-principal+jwt", header.GetProperty("typ").GetString());
        Assert.Equal(HandoffIssuer, payload.GetProperty("iss").GetString());
        Assert.Equal(HandoffAudience, payload.GetProperty("aud").GetString());
        Assert.Equal(TokenIssuer, payload.GetProperty("pis").GetString());
        Assert.Equal("player-1", payload.GetProperty("psub").GetString());
        Assert.Equal("Account", payload.GetProperty("pkind").GetString());
        Assert.Equal("POST", payload.GetProperty("htm").GetString());
        Assert.Equal("/sessions/s-1/act?turn=2", payload.GetProperty("htu").GetString());
        Assert.False(string.IsNullOrEmpty(payload.GetProperty("rid").GetString()));
        Assert.Equal(30, payload.GetProperty("exp").GetInt64() - payload.GetProperty("iat").GetInt64());
    }

    /// <summary>S3.7 — no log line, span attribute or metric label carries the bearer token or the
    /// assertion, across an accepted and a refused request.</summary>
    [Fact]
    public async Task S3_7_neither_the_token_nor_the_assertion_reaches_logs_spans_or_metrics()
    {
        var recorded = new ConcurrentQueue<string>();
        using var spans = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                recorded.Enqueue(activity.DisplayName);
                foreach (var tag in activity.TagObjects)
                {
                    recorded.Enqueue($"{tag.Key}={tag.Value}");
                }

                foreach (var activityEvent in activity.Events)
                {
                    foreach (var tag in activityEvent.Tags)
                    {
                        recorded.Enqueue($"{tag.Key}={tag.Value}");
                    }
                }
            },
        };
        ActivitySource.AddActivityListener(spans);

        using var meters = new MeterListener
        {
            InstrumentPublished = (instrument, listener) => listener.EnableMeasurementEvents(instrument),
        };
        void Measured<T>(Instrument instrument, T value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
        {
            foreach (var tag in tags)
            {
                recorded.Enqueue($"{instrument.Name}:{tag.Key}={tag.Value}");
            }
        }

        meters.SetMeasurementEventCallback<long>(Measured);
        meters.SetMeasurementEventCallback<int>(Measured);
        meters.SetMeasurementEventCallback<double>(Measured);
        meters.Start();

        var logs = new CapturingLoggerProvider();
        await using var workload = await FakeWorkload.StartAsync();
        await using var edge = Terminate(workload.BaseAddress, logs);
        using var client = edge.CreateClient();

        var token = Token("player-1");
        using (var accepted = new HttpRequestMessage(HttpMethod.Post, "/sessions"))
        {
            accepted.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(accepted);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using (var refused = await client.PostAsync("/sessions", null))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        }

        var assertion = Assert.Single(Assert.Single(workload.Requests).Headers["SubZeroDev-Principal-Assertion"]);
        Assert.NotEmpty(logs.Lines);
        var everything = string.Join("\n", logs.Lines.Concat(recorded));
        foreach (var secret in new[] { token, assertion, token.Split('.')[2], assertion.Split('.')[2] })
        {
            Assert.DoesNotContain(secret, everything, StringComparison.Ordinal);
        }
    }

    /// <summary>S3.6 — terminate mode with no handoff settings, or with a signing key file that cannot
    /// be read, and pass-through mode with handoff settings, each refuse to start, naming the
    /// setting.</summary>
    [Theory]
    [InlineData("Terminate", false, "present", "GameEdge:Handoff")]
    [InlineData("Terminate", true, "missing", "GameEdge:Handoff:SigningKeyPath")]
    [InlineData("Terminate", true, "public", "GameEdge:Handoff:SigningKeyPath")]
    [InlineData("Terminate", true, "garbage", "GameEdge:Handoff:SigningKeyPath")]
    [InlineData("PassThrough", true, "present", "GameEdge:Handoff")]
    [InlineData(null, false, "present", "GameEdge:CredentialMode")]
    public async Task S3_6_a_mode_without_the_settings_it_requires_or_with_ones_it_refuses_fails_startup(
        string? mode, bool withHandoff, string keyFile, string named)
    {
        var path = keyFile switch
        {
            "present" => signingKeyPath,
            "missing" => Path.Combine(directory, "absent.pem"),
            "public" => Write("public.pem", handoffKey.ExportSubjectPublicKeyInfoPem()),
            _ => Write("garbage.pem", "not a key"),
        };

        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("GameEdge:WorkloadBaseAddress", "http://127.0.0.1:1");
            builder.UseSetting("GameEdge:ForwardTimeout", "00:00:05");
            builder.UseSetting("GameEdge:ReadinessTimeout", "00:00:01");
            if (mode is not null)
            {
                builder.UseSetting("GameEdge:CredentialMode", mode);
            }

            if (withHandoff)
            {
                Handoff(builder, path);
            }

        });

        var thrown = Assert.ThrowsAny<Exception>(factory.CreateClient);
        Assert.Contains($"'{named}'", Flatten(thrown), StringComparison.Ordinal);
    }

    /// <summary>S3.6 — each handoff field is required, and a lifetime under a second is refused,
    /// naming the field.</summary>
    [Theory]
    [InlineData("Issuer", "")]
    [InlineData("Audience", "")]
    [InlineData("KeyId", "")]
    [InlineData("SigningKeyPath", "")]
    [InlineData("Lifetime", "")]
    [InlineData("Lifetime", "00:00:00.500")]
    public async Task S3_6_each_handoff_field_is_checked_by_name(string field, string value)
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("GameEdge:WorkloadBaseAddress", "http://127.0.0.1:1");
            builder.UseSetting("GameEdge:ForwardTimeout", "00:00:05");
            builder.UseSetting("GameEdge:ReadinessTimeout", "00:00:01");
            builder.UseSetting("GameEdge:CredentialMode", "Terminate");
            Handoff(builder, signingKeyPath);
            builder.UseSetting($"GameEdge:Handoff:{field}", value);
        });

        var thrown = Assert.ThrowsAny<Exception>(factory.CreateClient);
        Assert.Contains($"'GameEdge:Handoff:{field}'", Flatten(thrown), StringComparison.Ordinal);
    }

    private WebApplicationFactory<Program> Terminate(string workloadBaseAddress, ILoggerProvider? logs = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("GameEdge:WorkloadBaseAddress", workloadBaseAddress);
            builder.UseSetting("GameEdge:ForwardTimeout", "00:00:05");
            builder.UseSetting("GameEdge:ReadinessTimeout", "00:00:01");
            builder.UseSetting("GameEdge:CredentialMode", "Terminate");
            Handoff(builder, signingKeyPath);
            if (logs is not null)
            {
                // Platform composes Serilog with `writeToProviders: false`; replacing the factory is
                // what makes every `ILogger<T>` reach the capture (see StreamingTests).
                builder.ConfigureTestServices(services => services.AddSingleton(
                    LoggerFactory.Create(logging => logging.AddProvider(logs).SetMinimumLevel(LogLevel.Trace))));
            }
        });

    private static void Handoff(IWebHostBuilder builder, string keyPath)
    {
        builder.UseSetting("GameEdge:Handoff:Issuer", HandoffIssuer);
        builder.UseSetting("GameEdge:Handoff:Audience", HandoffAudience);
        builder.UseSetting("GameEdge:Handoff:KeyId", HandoffKeyId);
        builder.UseSetting("GameEdge:Handoff:SigningKeyPath", keyPath);
        builder.UseSetting("GameEdge:Handoff:Lifetime", "00:00:30");
    }

    private static ECDsa PinIssuer()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var parameters = key.ExportParameters(includePrivateParameters: false);
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
                    kid = IssuerKeyId,
                    use = "sig",
                    alg = "ES256",
                },
            },
        });

        Environment.SetEnvironmentVariable("Platform__Identity__Bearer__game__Issuer", TokenIssuer);
        Environment.SetEnvironmentVariable("Platform__Identity__Bearer__game__SigningKeys", jwks);
        Environment.SetEnvironmentVariable("Platform__Identity__Bearer__game__Audiences__0", TokenAudience);
        Environment.SetEnvironmentVariable("Platform__Identity__Bearer__game__Algorithms__0", "ES256");
        return key;
    }

    private static string Token(string subject)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var header = Encode(new { alg = "ES256", kid = IssuerKeyId, typ = "JWT" });
        var payload = Encode(new { iss = TokenIssuer, sub = subject, aud = TokenAudience, iat = now, nbf = now, exp = now + 300 });
        var input = header + "." + payload;
        return input + "." + Base64Url.EncodeToString(IssuerKey.SignData(Encoding.ASCII.GetBytes(input), HashAlgorithmName.SHA256));
    }

    private (JsonElement Header, JsonElement Payload) Verify(string assertion)
    {
        var parts = assertion.Split('.');
        Assert.Equal(3, parts.Length);
        Assert.True(handoffKey.VerifyData(
            Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]),
            Base64Url.DecodeFromChars(parts[2]),
            HashAlgorithmName.SHA256));
        return (
            JsonDocument.Parse(Base64Url.DecodeFromChars(parts[0])).RootElement,
            JsonDocument.Parse(Base64Url.DecodeFromChars(parts[1])).RootElement);
    }

    private static string Encode(object value) => Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(value));

    private string Write(string name, string content)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static string Flatten(Exception exception)
    {
        var text = new StringBuilder();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            text.AppendLine(current.Message);
        }

        return text.ToString();
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<string> Lines { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(this);

        public void Dispose()
        {
        }

        private sealed class Logger(CapturingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
            {
                owner.Lines.Enqueue(state.ToString() ?? string.Empty);
                return null;
            }

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                owner.Lines.Enqueue(formatter(state, exception));
                foreach (var pair in (state as IEnumerable<KeyValuePair<string, object?>>) ?? [])
                {
                    owner.Lines.Enqueue($"{pair.Key}={pair.Value}");
                }

                if (exception is not null)
                {
                    owner.Lines.Enqueue(exception.ToString());
                }
            }
        }
    }
}
