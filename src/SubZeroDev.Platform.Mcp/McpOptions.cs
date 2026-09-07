namespace SubZeroDev.Platform.Mcp;

/// <summary>Which registered tools configuration exposes. Not part of the contract's declared
/// types (design/20-contract.md § Public surface 10 says only that "Mcp exposes tool producer
/// registration and exposure configuration" without naming its shape) — this is the module's own
/// answer, and default closed: a tool absent from <see cref="ExposedTools"/> is registered but
/// never listed or callable (I-M3).</summary>
public sealed class McpOptions
{
    /// <summary>The names of every tool configuration exposes. Empty by default, so installing a
    /// producer never exposes anything on its own — a deployment must say so explicitly.</summary>
    public IReadOnlySet<ToolName> ExposedTools { get; init; } = new HashSet<ToolName>();
}
