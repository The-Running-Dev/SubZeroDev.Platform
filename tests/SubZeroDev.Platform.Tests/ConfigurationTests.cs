using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using SubZeroDev.Platform.Hosting;

namespace SubZeroDev.Platform.Tests;

/// <summary>The brief's second CI assertion: a deliberately broken configuration aborts startup
/// with a named error. Every case here is a startup abort, not a first-request failure.</summary>
public sealed class ConfigurationTests
{
    [Fact]
    public void A_missing_retention_window_names_the_setting_and_where_to_put_it()
    {
        var error = AbortOf(settings => settings.Remove("Platform:Outbox:ProcessedRetention"));

        Assert.Equal("Configuration", error.Code);
        Assert.Contains("Platform:Outbox:ProcessedRetention", error.Detail, StringComparison.Ordinal);
        Assert.Contains("appsettings.json", error.Detail, StringComparison.Ordinal);
        Assert.Equal("MissingRequiredSetting", error.Inner?.Code);
    }

    [Fact]
    public void A_missing_connection_string_aborts_startup()
    {
        var error = AbortOf(settings => settings.Remove("Platform:Persistence:ConnectionString"));

        Assert.Equal("MissingRequiredSetting", error.Inner?.Code);
        Assert.Contains("Platform:Persistence:ConnectionString", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_poison_window_that_is_not_longer_than_the_processed_one_names_both()
    {
        var error = AbortOf(settings => settings["Platform:Outbox:PoisonedRetention"] = "1.00:00:00");

        Assert.Equal("InconsistentSettings", error.Inner?.Code);
        Assert.Contains("Platform:Outbox:PoisonedRetention", error.Detail, StringComparison.Ordinal);
        Assert.Contains("Platform:Outbox:ProcessedRetention", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_drain_window_that_is_not_shorter_than_the_claim_window_names_both()
    {
        var error = AbortOf(settings =>
        {
            settings["Platform:Outbox:ClaimWindow"] = "00:05:00";
            settings["Platform:Hosting:GracefulShutdownDrainWindow"] = "00:10:00";
        });

        Assert.Equal("InconsistentSettings", error.Inner?.Code);
        Assert.Contains("Platform:Hosting:GracefulShutdownDrainWindow", error.Detail, StringComparison.Ordinal);
        Assert.Contains("Platform:Outbox:ClaimWindow", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_backoff_factor_of_one_names_the_constraint()
    {
        var error = AbortOf(settings => settings["Platform:Outbox:RetryBackoffFactor"] = "1");

        Assert.Equal("InvalidSetting", error.Inner?.Code);
        Assert.Contains("Platform:Outbox:RetryBackoffFactor", error.Detail, StringComparison.Ordinal);
        Assert.Contains("greater than 1", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_prune_batch_beyond_its_upper_bound_is_rejected()
    {
        var error = AbortOf(settings => settings["Platform:Outbox:PruneBatchSize"] = "50000");

        Assert.Equal("InvalidSetting", error.Inner?.Code);
        Assert.Contains("Platform:Outbox:PruneBatchSize", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unparseable_duration_is_rejected_rather_than_defaulted()
    {
        var error = AbortOf(settings => settings["Platform:Outbox:ClaimWindow"] = "five minutes");

        Assert.Equal("InvalidSetting", error.Inner?.Code);
        Assert.Contains("Platform:Outbox:ClaimWindow", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_valid_configuration_starts()
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = "Production",
        });

        builder.Configuration.AddInMemoryCollection(Settings.Required());

        var exception = Record.Exception(() => builder.AddPlatformWebHost());

        Assert.Null(exception);
    }

    private static HostStartupError AbortOf(Action<Dictionary<string, string?>> break_)
    {
        var settings = Settings.Required();
        break_(settings);

        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = "Production",
        });

        builder.Configuration.AddInMemoryCollection(settings);

        var thrown = Assert.Throws<PlatformStartupException>(() => builder.AddPlatformWebHost());
        return Assert.IsType<HostStartupError>(thrown.Error);
    }
}
