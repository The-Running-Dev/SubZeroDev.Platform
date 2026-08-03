using Microsoft.AspNetCore.Builder;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using SubZeroDev.Platform.Hosting;

namespace SubZeroDev.Platform.Tests;

public sealed class WorkerProbeTests
{
    [Fact]
    public void A_port_already_bound_aborts_startup_naming_the_setting()
    {
        var occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.Start();
        var port = ((IPEndPoint)occupied.LocalEndpoint).Port;

        try
        {
            var settings = Settings.Required();
            settings["Platform:Hosting:WorkerProbePort"] = port.ToString();

            var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
            {
                EnvironmentName = "Production",
            });

            builder.Configuration.AddInMemoryCollection(settings);

            var thrown = Assert.Throws<PlatformStartupException>(() => builder.AddPlatformWorkerHost());
            var error = Assert.IsType<HostStartupError>(thrown.Error);

            Assert.Equal("ProbeBindFailed", error.Code);

            // A silent fallback port would make the probe surface unfindable on a box running two
            // installations, so the setting has to be named.
            Assert.Contains("Platform:Hosting:WorkerProbePort", error.Detail, StringComparison.Ordinal);
            Assert.Contains(port.ToString(), error.Detail, StringComparison.Ordinal);
        }
        finally
        {
            occupied.Stop();
        }
    }

    [Fact]
    public void A_free_port_starts()
    {
        var settings = Settings.Required();
        settings["Platform:Hosting:WorkerProbePort"] = FreePort().ToString();

        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = "Production",
        });

        builder.Configuration.AddInMemoryCollection(settings);

        Assert.Null(Record.Exception(() => builder.AddPlatformWorkerHost()));
    }

    [Fact]
    public async Task The_worker_serves_its_probes_on_loopback_and_nowhere_else()
    {
        var port = FreePort();
        var settings = Settings.Required();
        settings["Platform:Hosting:WorkerProbePort"] = port.ToString();

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Production",
            ApplicationName = typeof(WorkerProbeTests).Assembly.GetName().Name,
        });

        builder.Configuration.AddInMemoryCollection(settings);
        builder.AddPlatformWorkerHost();

        var app = builder.Build();
        await app.StartAsync(CancellationToken.None);

        try
        {
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            var response = await client.GetAsync("/health/ready", CancellationToken.None);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.False(BoundOnAnyNonLoopbackAddress(port));
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    private static bool BoundOnAnyNonLoopbackAddress(int port)
    {
        // If Kestrel had bound the wildcard address, binding a non-loopback local address on the
        // same port would fail. It succeeding is the evidence that the probe is loopback-only.
        var address = Dns.GetHostAddresses(Dns.GetHostName())
            .FirstOrDefault(candidate =>
                candidate.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(candidate));

        if (address is null)
        {
            return false;
        }

        try
        {
            var listener = new TcpListener(address, port);
            listener.Start();
            listener.Stop();
            return false;
        }
        catch (SocketException)
        {
            return true;
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
