using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Hosting;
using SubZeroDev.Platform.Observability;

namespace SubZeroDev.Platform.Tests;

/// <summary>S8.2 (partial): <c>Platform:Telemetry</c> binds a default log directory and an absent
/// OTLP endpoint, and both host registration paths reject a present-but-malformed one.</summary>
public sealed class TelemetryOptionsTests
{
    [Fact]
    public void An_absent_OtlpEndpoint_starts_the_host_with_no_exporter_configured()
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = "Production",
        });
        builder.Configuration.AddInMemoryCollection(Settings.Required());

        var exception = Record.Exception(() => builder.AddPlatformWebHost());

        Assert.Null(exception);
    }

    [Fact]
    public void A_relative_OtlpEndpoint_is_rejected_rather_than_silently_ignored()
    {
        var settings = Settings.Required();
        settings["Platform:Telemetry:OtlpEndpoint"] = "/not-absolute";

        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = "Production",
        });
        builder.Configuration.AddInMemoryCollection(settings);

        var thrown = Assert.Throws<PlatformStartupException>(() => builder.AddPlatformWebHost());
        var error = Assert.IsType<HostStartupError>(thrown.Error);

        Assert.Equal("InvalidSetting", error.Inner?.Code);
        Assert.Contains("Platform:Telemetry:OtlpEndpoint", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_non_http_OtlpEndpoint_scheme_is_rejected()
    {
        var settings = Settings.Required();
        settings["Platform:Telemetry:OtlpEndpoint"] = "ftp://collector.example/otlp";

        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = "Production",
        });
        builder.Configuration.AddInMemoryCollection(settings);

        var thrown = Assert.Throws<PlatformStartupException>(() => builder.AddPlatformWebHost());
        var error = Assert.IsType<HostStartupError>(thrown.Error);

        Assert.Equal("InvalidSetting", error.Inner?.Code);
    }

    [Fact]
    public void A_valid_absolute_http_OtlpEndpoint_starts_the_host()
    {
        var settings = Settings.Required();
        settings["Platform:Telemetry:OtlpEndpoint"] = "http://collector.example:4318";

        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = "Production",
        });
        builder.Configuration.AddInMemoryCollection(settings);

        var exception = Record.Exception(() => builder.AddPlatformWebHost());

        Assert.Null(exception);
    }

    [Fact]
    public void Standalone_observability_accepts_an_absent_OtlpEndpoint()
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = "Production",
        });

        var exception = Record.Exception(() => builder.AddPlatformObservability());

        Assert.Null(exception);
    }

    [Theory]
    [InlineData("/not-absolute")]
    [InlineData("ftp://collector.example/otlp")]
    public void Standalone_observability_rejects_a_malformed_OtlpEndpoint(string endpoint)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Platform:Telemetry:OtlpEndpoint"] = endpoint,
        };
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = "Production",
        });
        builder.Configuration.AddInMemoryCollection(settings);

        var thrown = Assert.Throws<ObservabilityStartupException>(() => builder.AddPlatformObservability());
        var error = Assert.IsType<ConfigurationError>(thrown.Error);

        Assert.Equal("InvalidSetting", error.Code);
        Assert.False(error.IsRetryable);
        Assert.Contains("Platform:Telemetry:OtlpEndpoint", error.Detail, StringComparison.Ordinal);
        Assert.Contains("absolute http or https URI", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Standalone_observability_accepts_a_valid_absolute_http_OtlpEndpoint()
    {
        var settings = new Dictionary<string, string?>
        {
            ["Platform:Telemetry:OtlpEndpoint"] = "http://collector.example:4318",
        };
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = "Production",
        });
        builder.Configuration.AddInMemoryCollection(settings);

        var exception = Record.Exception(() => builder.AddPlatformObservability());

        Assert.Null(exception);
    }
}
