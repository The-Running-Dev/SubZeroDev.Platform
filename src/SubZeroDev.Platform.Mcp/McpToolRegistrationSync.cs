using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace SubZeroDev.Platform.Mcp;

/// <summary>Populates the SDK's tool collection from the frozen catalogue, once. Registered after
/// <see cref="McpStartupValidation"/> — <c>IHostedService.StartAsync</c> runs in registration order,
/// so <see cref="ToolCatalogue.Initialize"/> has already run and <see cref="IToolCatalogue.Exposed"/>
/// is the whole, frozen set by the time this reads it. Runs once and never again, on the same terms
/// I-M1 already holds the catalogue itself to: nothing here registers, unregisters or re-exposes a
/// tool at runtime — it only projects what S14 already decided onto the SDK's own collection.
///
/// <para>Reads <see cref="IOptions{TOptions}"/> rather than a directly injected
/// <c>McpServerPrimitiveCollection&lt;McpServerTool&gt;</c>: the SDK registers no such service on its
/// own, and <c>IOptions&lt;McpServerOptions&gt;.Value</c> is the one instance every session's
/// <c>McpServer.ServerOptions</c> resolves to, since the options system caches it (verified against
/// the built package, not from memory, per AGENTS.md § Verification).</para></summary>
internal sealed class McpToolRegistrationSync(
    IToolCatalogue catalogue,
    IOptions<McpServerOptions> serverOptions,
    IServiceProvider rootServices) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var toolCollection = serverOptions.Value.ToolCollection
            ?? throw new InvalidOperationException("McpServerOptions.ToolCollection was not initialized.");
        foreach (var registration in catalogue.Exposed)
        {
            toolCollection.Add(new PlatformMcpTool(registration, rootServices));
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
