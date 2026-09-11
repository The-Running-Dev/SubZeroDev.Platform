using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;
using SubZeroDev.Platform.Abstractions;

namespace SubZeroDev.Platform.Mcp;

/// <summary>D5-S14's module: the frozen tool catalogue and its startup checks. A host also
/// registers one or more <see cref="IToolProducer"/> implementations of its own — a manifest
/// projection, a product-owned fixed table, or both — and may register a <see cref="McpOptions"/>
/// naming which of the tools they supply are exposed. Depends only on the framework's own
/// contracts — no knowledge of Identity, Organizations, Billing or Licensing.</summary>
public sealed class McpModule : IPlatformModule
{
    /// <inheritdoc/>
    public ModuleName Name { get; } = new("Mcp");

    /// <inheritdoc/>
    public IReadOnlyCollection<ModuleName> DependsOn { get; } = [];

    /// <inheritdoc/>
    public void Register(IServiceCollection services)
    {
        services.TryAddSingleton<McpOptions>();
        services.TryAddSingleton<ToolCatalogue>();
        services.TryAddSingleton<IToolCatalogue>(provider => provider.GetRequiredService<ToolCatalogue>());

        // Runs once, at startup, and never again — the frozen catalogue this produces is what
        // I-M1 requires. A plain IHostedService, exactly like Licensing's own startup-only
        // verification: a Platform background-work registration is a recurring tick by
        // construction, and this must not become one.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, McpStartupValidation>());

        // D5-S15: the transport, the session and the filter pipeline. Registered here so a host
        // never touches an SDK type directly — only SubZeroDev.Platform.Mcp.MapPlatformMcp, the
        // one call a host makes to map the endpoints (I-M9).
        services
            .AddMcpServer(options =>
                // The SDK leaves ToolCollection null until WithTools(...) is called at least once
                // (verified against the built package, not from memory, per AGENTS.md §
                // Verification) — Platform never calls that, since McpToolRegistrationSync populates
                // the collection itself from the frozen catalogue, so it is constructed here instead.
                options.ToolCollection = new McpServerPrimitiveCollection<McpServerTool>())
            .WithHttpTransport(options =>
                // Stateful: the connection's session must persist and stay bound to one principal
                // for its lifetime (S15.1, I-M5) — the SDK's default is Stateless, under which every
                // request is its own session and the SDK's own session-principal binding never
                // engages at all (verified against the built package, not from memory, per
                // AGENTS.md § Verification).
                options.SessionMode = HttpServerSessionMode.Stateful)
            .WithRequestFilters(McpToolFilters.Install);

        // Runs after McpStartupValidation (hosted services start in registration order): projects
        // the now-frozen catalogue's exposed set onto the SDK's own tool collection, once.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, McpToolRegistrationSync>());
    }
}
