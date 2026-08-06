using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using SubZeroDev.Platform.Hosting;

namespace SubZeroDev.Platform.Tests;

/// <summary>S8.2 (partial): <c>Platform:Telemetry</c> binds a default log directory and an absent
/// OTLP endpoint, and rejects a present-but-malformed one, the same way every other setting in
/// <c>PlatformOptionsBinder</c> does.</summary>
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
}
