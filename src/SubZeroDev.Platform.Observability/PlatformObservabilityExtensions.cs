using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using SubZeroDev.Platform.Abstractions;

namespace SubZeroDev.Platform.Observability;

/// <summary>Observability's registration call.</summary>
public static class PlatformObservabilityExtensions
{
    /// <summary>Wires telemetry and trace-context propagation. Called by both forms of the standard
    /// registration call, and exposed separately for a consumer that wants telemetry without a
    /// Platform host.</summary>
    /// <param name="builder">The host builder.</param>
    /// <returns>The same builder, so calls chain.</returns>
    public static IHostApplicationBuilder AddPlatformObservability(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton<ITraceContextCodec, TraceContextCodec>();

        return builder;
    }
}
