using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;

namespace SubZeroDev.Platform.Mcp;

/// <summary>Projects one exposed <see cref="ToolRegistration"/> onto the SDK's <c>McpServerTool</c> —
/// the boundary design/20-contract.md § *Types* 10 names — and runs the fixed invocation order
/// (design/20-contract.md § *Public surface* 10) entirely inside <see cref="InvokeAsync"/>: catalogue
/// lookup, tenant resolution, argument parse, authorize, entitlement, invoke inside an operation scope
/// of its own, audit. Built once per exposed tool by <see cref="McpToolRegistrationSync"/>, after the
/// catalogue is frozen — never re-registered, on the same terms I-M1 already holds S14 to.</summary>
internal sealed class PlatformMcpTool(ToolRegistration registration, IServiceProvider rootServices) : McpServerTool
{
    /// <inheritdoc/>
    public override Tool ProtocolTool { get; } = new()
    {
        Name = registration.Definition.Name.Value,
        Description = registration.Definition.Description,
        InputSchema = registration.Definition.ParameterSchema,
    };

    /// <inheritdoc/>
    public override IReadOnlyList<object> Metadata { get; } = [];

    /// <inheritdoc/>
    public override async ValueTask<CallToolResult> InvokeAsync(
        RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken)
    {
        var services = request.Services ?? rootServices;
        var tool = registration.Definition.Name;

        // Step 1 — catalogue lookup. Re-asserted here (rather than trusted from construction) so the
        // trace records it as its own step of this invocation, exactly as S15.3 requires.
        var catalogue = services.GetRequiredService<IToolCatalogue>();
        if (!catalogue.TryGetExposed(tool, out var current))
        {
            return McpToolResults.UnknownTool(tool);
        }

        // Step 2 — resolve the tenant.
        var tenantResolution = services.GetRequiredService<TenantResolutionChain>();
        var tenant = await tenantResolution.ResolveAsync(cancellationToken).ConfigureAwait(false);

        // Step 3 — parse arguments against the declared schema.
        var arguments = request.Params?.Arguments ?? new Dictionary<string, JsonElement>();
        if (!ToolArgumentSchema.IsValid(current.Definition.ParameterSchema, arguments))
        {
            return McpToolResults.InvalidArguments(tool);
        }

        var principal = services.GetRequiredService<ICurrentPrincipal>().Current;
        var resource = ToolResourceScope.TryResolve(current.Definition.ParameterSchema, tool, arguments);
        var scopeFactory = services.GetRequiredService<IOperationScopeFactory>();
        var auditWriter = services.GetRequiredService<IAuditWriter>();

        // Step 4 — authorize the declared permission, scoped to the parsed resource when the schema
        // names one. Never resource-scoped when it does not (I-R7's Mcp analogue).
        var authorization = services.GetRequiredService<IAuthorizationEvaluator>();
        var decision = await authorization
            .EvaluateAsync(current.Definition.RequiredPermission, resource, cancellationToken)
            .ConfigureAwait(false);

        if (decision.Outcome != AuthorizationOutcome.Allowed)
        {
            // A denial is audited too (S15.9 counts every invocation of a known tool), inside its own
            // scope — never the one a successful invocation would open around producer work.
            using var deniedScope = scopeFactory.Begin(tenant, principal);
            await auditWriter
                .WriteAsync(new AuditAction(tool.Value), resource, AuditOutcome.Denied, AuditClass.Required, cancellationToken)
                .ConfigureAwait(false);
            return McpToolResults.Forbidden(tool);
        }

        // Step 5 — check entitlement, only when the tool declares a feature.
        if (current.Definition.RequiredFeature is { } feature)
        {
            var entitlement = services.GetRequiredService<IEntitlementEvaluator>();
            var entitlementDecision = await entitlement.EvaluateAsync(feature, cancellationToken).ConfigureAwait(false);
            if (!entitlementDecision.Granted)
            {
                using var deniedScope = scopeFactory.Begin(tenant, principal);
                await auditWriter
                    .WriteAsync(new AuditAction(tool.Value), resource, AuditOutcome.Denied, AuditClass.Required, cancellationToken)
                    .ConfigureAwait(false);
                return McpToolResults.NotEntitled(tool);
            }
        }

        // Step 6 — invoke inside an operation scope of its own. Authorization and entitlement both
        // ran before this line is reached (S15.4) — a denied call never opens this scope and never
        // reaches an invoker.
        var invokers = services.GetRequiredService<IEnumerable<IToolInvoker>>();
        var invoker = invokers.FirstOrDefault(candidate => candidate.Name == current.Producer);

        ToolInvocationResult result;
        AuditOutcome outcome;
        using (scopeFactory.Begin(tenant, principal))
        {
            try
            {
                result = invoker is null
                    ? ToolInvocationResult.Failure("No invoker is registered for this tool's producer.")
                    : await invoker.InvokeAsync(tool, arguments, cancellationToken).ConfigureAwait(false);
                outcome = result.IsError ? AuditOutcome.Failed : AuditOutcome.Allowed;
            }
            catch (OperationCanceledException)
            {
                // S15.11: the connection dropped mid-invocation. Propagate through the existing
                // cancellation plumbing rather than audit a call that never finished.
                throw;
            }
            catch
            {
                outcome = AuditOutcome.Failed;
                result = ToolInvocationResult.Failure("The tool failed to complete.");
            }

            // Step 7 — audit, with the tool as the action and no arguments.
            await auditWriter
                .WriteAsync(new AuditAction(tool.Value), resource, outcome, AuditClass.Required, cancellationToken)
                .ConfigureAwait(false);
        }

        return McpToolResults.FromInvocation(result);
    }
}

/// <summary>Builds the <c>CallToolResult</c> answers the fixed order's non-happy paths return. All in
/// one place so every "no existence disclosure" and "no argument value" rule (S15.6, S15.8) has one
/// spot to hold, rather than one per call site.</summary>
internal static class McpToolResults
{
    internal static CallToolResult UnknownTool(ToolName tool) =>
        Error(McpError.UnknownTool(tool).Detail);

    internal static CallToolResult InvalidArguments(ToolName tool) =>
        Error(McpError.InvalidArguments(tool).Detail);

    internal static CallToolResult Forbidden(ToolName tool) =>
        Error($"Tool '{tool}' was denied.");

    internal static CallToolResult NotEntitled(ToolName tool) =>
        Error($"Tool '{tool}' is not entitled.");

    internal static CallToolResult FromInvocation(ToolInvocationResult result) => new()
    {
        IsError = result.IsError,
        Content = [.. result.Content.Select(text => (ContentBlock)new TextContentBlock { Text = text })],
    };

    private static CallToolResult Error(string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = message }],
    };
}

/// <summary>Installed ahead of the SDK's own default handling (design/90-decisions.md, 2026-08-29):
/// a call naming a tool the frozen catalogue does not expose never reaches the SDK's built-in
/// tool-collection dispatch at all, so it can never produce the SDK's own existence-disclosing
/// answer, and never touches the SDK's authorization-metadata path (I-M11) — Platform's evaluator is
/// consulted only from inside <see cref="PlatformMcpTool"/>, once a call has already matched an
/// exposed tool.</summary>
internal static class McpToolFilters
{
    internal static void Install(IMcpRequestFilterBuilder builder)
    {
        builder.AddCallToolFilter(next => async (context, cancellationToken) =>
        {
            var catalogue = context.Services!.GetRequiredService<IToolCatalogue>();
            var name = context.Params?.Name;
            if (name is null || !catalogue.TryGetExposed(new ToolName(name), out _))
            {
                return McpToolResults.UnknownTool(new ToolName(name ?? string.Empty));
            }

            return await next(context, cancellationToken).ConfigureAwait(false);
        });

        // Defensive: the SDK's own ListTools handler already enumerates only what ToolCollection
        // holds, which McpToolRegistrationSync populates from catalogue.Exposed alone (S15.7). This
        // filter re-asserts the same boundary so listing never depends on that population step being
        // the only thing keeping an unexposed tool off the wire.
        builder.AddListToolsFilter(next => async (context, cancellationToken) =>
        {
            var catalogue = context.Services!.GetRequiredService<IToolCatalogue>();
            var result = await next(context, cancellationToken).ConfigureAwait(false);
            result.Tools = [.. result.Tools.Where(t => catalogue.TryGetExposed(new ToolName(t.Name), out _))];
            return result;
        });
    }
}
