using SubZeroDev.Platform.Abstractions;

namespace SubZeroDev.Platform.Mcp;

/// <summary>D5-S15's own error type (design/20-contract.md § 8). Authorization and entitlement
/// refusals are deliberately not here — they are raised by the same <see cref="IAuthorizationEvaluator"/>
/// and <see cref="IEntitlementEvaluator"/> the HTTP path uses, so a denial means the same thing on
/// both surfaces.</summary>
public sealed record McpError : PlatformError
{
    private McpError(string code, string detail)
        : base(code) => Detail = detail;

    /// <summary>Names the cause. Never an argument value (S15.8).</summary>
    public string Detail { get; }

    /// <inheritdoc/>
    public override bool IsRetryable => false;

    /// <summary>The name is unregistered, or registered and not exposed. Both answer identically —
    /// never distinguished, because "forbidden" would confirm the tool exists (I-M4).</summary>
    /// <param name="tool">The tool name that was called or listed.</param>
    /// <returns>The error.</returns>
    public static McpError UnknownTool(ToolName tool) =>
        new(nameof(UnknownTool), $"Tool '{tool}' is unknown.");

    /// <summary>The call's arguments fail the tool's declared schema. Never names an argument value —
    /// an argument that failed validation is as likely to be a secret as one that passed.</summary>
    /// <param name="tool">The tool that was called.</param>
    /// <returns>The error.</returns>
    public static McpError InvalidArguments(ToolName tool) =>
        new(nameof(InvalidArguments), $"Arguments for tool '{tool}' do not satisfy its declared schema.");

    /// <summary>A session request presented a principal other than the one the session was
    /// established with. Ends the exchange — a new principal means a new connection.</summary>
    /// <returns>The error.</returns>
    public static McpError ConnectionUnauthenticated() =>
        new(
            nameof(ConnectionUnauthenticated),
            "The session's connection presented a principal other than the one it was established with.");
}
