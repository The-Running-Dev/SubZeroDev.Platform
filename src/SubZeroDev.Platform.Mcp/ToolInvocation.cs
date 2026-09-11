using System.Text.Json;

namespace SubZeroDev.Platform.Mcp;

/// <summary>What a tool's execution answered. Platform's own shape — the module projects it to the
/// SDK's <c>CallToolResult</c> at the boundary, the same way <see cref="ToolDefinition"/> projects to
/// the SDK's <c>Tool</c>.</summary>
/// <param name="IsError">Whether the tool's own work failed. Distinct from a denial: authorization and
/// entitlement refusals never reach a producer, so they never construct one of these.</param>
/// <param name="Content">Plain text content returned to the caller. Never an argument value, and
/// never a credential — the invocation surface has no route to either.</param>
public sealed record ToolInvocationResult(bool IsError, IReadOnlyList<string> Content)
{
    /// <summary>Builds a successful result.</summary>
    /// <param name="content">The content to return.</param>
    /// <returns>The result.</returns>
    public static ToolInvocationResult Success(params string[] content) => new(false, content);

    /// <summary>Builds a failed result — the tool was invoked and its own work did not complete.</summary>
    /// <param name="content">The content to return.</param>
    /// <returns>The result.</returns>
    public static ToolInvocationResult Failure(params string[] content) => new(true, content);
}

/// <summary>Executes a tool's declared behavior. <see cref="IToolProducer"/> (S14) supplies only a
/// tool's definition — its schema, required permission and required feature — because a manifest-
/// projecting producer has no .NET method to invoke and no shape that added an execution member to
/// that interface could avoid privileging the other kind of producer (I-M7) or drifting an already-
/// shipped S14 signature. An invoker is the companion seam S15 adds: registered under the same
/// <see cref="ToolProducerName"/> as the producer whose tools it executes, and reached only after
/// authorization and entitlement have both allowed the call (S15.4) — nothing before that point may
/// call it.</summary>
public interface IToolInvoker
{
    /// <summary>The producer name this invoker executes on behalf of. Matched against
    /// <see cref="ToolRegistration.Producer"/>, never against the tool name itself, so one invoker can
    /// serve every tool a producer supplies.</summary>
    ToolProducerName Name { get; }

    /// <summary>Executes one tool call. Never reached for a call authorization or entitlement denied,
    /// and never handed a credential — arguments are exactly what the caller parsed against the
    /// declared schema.</summary>
    /// <param name="tool">The tool being invoked.</param>
    /// <param name="arguments">The call's arguments, already parsed against the tool's declared
    /// schema.</param>
    /// <param name="cancellationToken">Cancels the invocation — including when the connection drops
    /// mid-call (S15.11).</param>
    /// <returns>What the tool's execution answered.</returns>
    ValueTask<ToolInvocationResult> InvokeAsync(
        ToolName tool,
        IDictionary<string, JsonElement> arguments,
        CancellationToken cancellationToken);
}
