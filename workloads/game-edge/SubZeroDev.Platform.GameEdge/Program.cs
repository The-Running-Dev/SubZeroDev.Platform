using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.GameEdge;
using SubZeroDev.Platform.Hosting;
using SubZeroDev.Platform.Identity;

var builder = WebApplication.CreateBuilder(args);

var section = builder.Configuration.GetSection("GameEdge");

// Before binding: a Handoff section missing a positional field would otherwise fail inside the
// binder, naming a constructor parameter rather than the setting.
if (CredentialSettings.Validate(section) is { Count: > 0 } credentialErrors)
{
    throw new InvalidOperationException(
        "Configuration section 'GameEdge' is invalid: " + string.Join(" ", credentialErrors));
}

if (TrustedProxySettings.Validate(section) is { Count: > 0 } proxyErrors)
{
    throw new InvalidOperationException(
        "Configuration section 'GameEdge:TrustedProxies' is invalid: " + string.Join(" ", proxyErrors));
}

if (AbuseLimitSettings.Validate(section) is { Count: > 0 } limitErrors)
{
    throw new InvalidOperationException(
        "Configuration section 'GameEdge:AbuseLimits' is invalid: " + string.Join(" ", limitErrors));
}

var options = section.Get<GameEdgeOptions>();

// `required` binds nothing: it is a compile-time obligation on the initialiser, and the
// configuration binder does not enforce it. A section that exists but omits WorkloadBaseAddress
// therefore binds to a null Uri, and the host starts, reports liveness healthy and fails every
// forward with an unhandled 500 — which is the outcome "no default" exists to prevent, arrived at
// one request later. appsettings.json always carries the section, so a null check alone would
// never fire; each setting is checked for itself.
if (options is null
    || options.WorkloadBaseAddress is not { IsAbsoluteUri: true }
    || options.ForwardTimeout <= TimeSpan.Zero
    || options.ReadinessTimeout <= TimeSpan.Zero)
{
    throw new InvalidOperationException(
        "Configuration section 'GameEdge' is required: WorkloadBaseAddress must be an absolute URI, "
        + "and ForwardTimeout and ReadinessTimeout must both be positive.");
}

if (StreamingRoutes.Validate(options) is { Count: > 0 } streamingErrors)
{
    throw new InvalidOperationException(
        "Configuration section 'GameEdge:StreamingRoutes' is invalid: " + string.Join(" ", streamingErrors));
}

builder.Services.AddSingleton(options);
builder.Services.AddSingleton<EdgeTelemetry>();
builder.Services.TryAddSingleton(TimeProvider.System);
builder.Services.AddSingleton<AbuseLimiter>();

// Terminate mode: Identity's bearer chain establishes the caller from the issuer's pinned keys, and
// the signer vouches for them to the workload. The key is read here, so an unreadable one stops the
// edge before it binds. Pass-through composes neither.
if (options.CredentialMode == CredentialMode.Terminate)
{
    // Before Identity is registered, because Identity's discovery fetches from the issuer at startup.
    if (PinnedKeys.Validate(builder.Configuration) is { Count: > 0 } pinningErrors)
    {
        throw new InvalidOperationException(
            "Configuration section 'Platform:Identity:Bearer' is invalid for 'Terminate' mode: "
            + string.Join(" ", pinningErrors));
    }

    builder.Services.AddSingleton(HandoffSigner.Load(options.Handoff!, TimeProvider.System));
    builder.Services.AddSingleton<IPlatformModule>(new IdentityModule());
}

// No cookie container. The factory's default handler keeps one, and the pooled handler serves every
// caller, so a workload's Set-Cookie was stored once and replayed on every later caller's request —
// one player's session handed to the next (#339). A cookie belongs to a caller, never to the edge.
// No redirect following either. The default handler chases a workload's 3xx itself — a request the
// caller never made, to wherever Location points — and hands back the destination's answer as the
// workload's. A redirect is the caller's to follow; whether Location reaches it is G3's ruling (#340).
builder.Services.AddHttpClient<IGameWorkloadForwarder, GameWorkloadForwarder>()
    .UseSocketsHttpHandler((handler, _) =>
    {
        handler.UseCookies = false;
        handler.AllowAutoRedirect = false;
    });

// The probe is a singleton because the check that holds it is one, and it asks the factory for a
// client per probe rather than capturing one — see GameWorkloadProbe's own note.
builder.Services.AddHttpClient(GameWorkloadProbe.HttpClientName);
builder.Services.TryAddSingleton<IGameWorkloadProbe, GameWorkloadProbe>();
builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IHealthCheck, GameWorkloadReadinessCheck>());

// The only mandatory Platform call. Health, readiness and correlation come with it.
builder.AddPlatformWebHost();

var app = builder.Build();

// Ordinary application code, registered the way any application registers a route — there is no
// AddGameEdge.
app.MapGameWorkloadForwarding();

app.Run();

/// <summary>Exposes the entry point to <c>WebApplicationFactory&lt;Program&gt;</c>.</summary>
public partial class Program;
