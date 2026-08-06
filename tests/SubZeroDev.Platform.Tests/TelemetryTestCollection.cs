namespace SubZeroDev.Platform.Tests;

/// <summary>Serialises every test that attaches a process-wide <see cref="System.Diagnostics.ActivityListener"/>
/// or that polls a shared log-file/async-buffer under timing pressure. <see cref="System.Diagnostics.ActivitySource"/>
/// listeners are process-global by construction — two such tests running in different xUnit
/// collections at the same time observe each other's activities, which turns an "exactly one span"
/// assertion into a race. Collection-serialising only these tests keeps the rest of the suite
/// parallel.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TelemetryTestCollection
{
    /// <summary>The collection name every telemetry test in this file group shares.</summary>
    public const string Name = "PlatformTelemetry";
}
