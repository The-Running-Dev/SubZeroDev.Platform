using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Hosting;
using SubZeroDev.Platform.Mcp;

namespace SubZeroDev.Platform.Tests;

/// <summary>D5-S14: a product can offer tools to an AI client from two independent sources — a
/// manifest it ships and its own code — with neither privileged over the other, nothing exposed
/// until configuration says so, and a tool that could ask for a password never reaching a running
/// catalogue.</summary>
public sealed class McpTests
{
    private static readonly PermissionName SamplePermission = new("Sample.Tool.Use");

    /// <summary>S14.1 — a manifest-projecting producer and a product-owned fixed-table producer
    /// both register through <see cref="IToolProducer"/>, and the catalogue treats them
    /// identically: no ordering, capability or schema-derivation difference between them.</summary>
    [Fact]
    public async Task Two_independent_producers_are_treated_identically_by_the_catalogue()
    {
        var manifestProjecting = new StubToolProducer(
            "manifest",
            new ToolDefinition(new ToolName("from-manifest"), "A manifest tool.", Schema(), SamplePermission, null));
        var fixedTable = new StubToolProducer(
            "fixed-table",
            new ToolDefinition(new ToolName("from-fixed-table"), "A fixed-table tool.", Schema(), SamplePermission, null));

        var (app, catalogue) = await StartWithProducersAsync(
            [manifestProjecting, fixedTable],
            exposed: [new ToolName("from-manifest"), new ToolName("from-fixed-table")]);

        try
        {
            Assert.Equal(2, catalogue.Exposed.Count);
            Assert.True(catalogue.TryGetExposed(new ToolName("from-manifest"), out var first));
            Assert.True(catalogue.TryGetExposed(new ToolName("from-fixed-table"), out var second));
            Assert.Equal(new ToolProducerName("manifest"), first.Producer);
            Assert.Equal(new ToolProducerName("fixed-table"), second.Producer);
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    /// <summary>S14.2 — <see cref="ToolDefinition"/> declares no exposure member. Exposure comes
    /// only from configuration.</summary>
    [Fact]
    public void ToolDefinition_declares_no_exposure_member()
    {
        var members = typeof(ToolDefinition).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name);

        Assert.DoesNotContain(members, name => name.Contains("Expos", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>S14.3 — a registered tool absent from the exposure configuration is not in the
    /// catalogue's exposed set, and looking it up answers exactly what looking up a name that was
    /// never registered answers.</summary>
    [Fact]
    public async Task A_registered_but_unexposed_tool_answers_identically_to_an_unregistered_name()
    {
        var producer = new StubToolProducer(
            "producer",
            new ToolDefinition(new ToolName("hidden"), "Not exposed.", Schema(), SamplePermission, null));

        var (app, catalogue) = await StartWithProducersAsync([producer], exposed: []);

        try
        {
            var hiddenFound = catalogue.TryGetExposed(new ToolName("hidden"), out var hiddenRegistration);
            var neverRegisteredFound = catalogue.TryGetExposed(new ToolName("never-registered"), out var neverRegistration);

            Assert.False(hiddenFound);
            Assert.False(neverRegisteredFound);
            Assert.Equal(neverRegistration, hiddenRegistration);
            Assert.DoesNotContain(catalogue.Exposed, registration => registration.Definition.Name == new ToolName("hidden"));
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    /// <summary>S14.4 — <see cref="IToolCatalogue"/> declares no member reaching an unexposed
    /// registration: no enumeration of all registrations, no exposure-ignoring lookup.</summary>
    [Fact]
    public void IToolCatalogue_declares_no_member_reaching_an_unexposed_registration()
    {
        Assert.Equal(
            ["Exposed", "TryGetExposed"],
            DeclaredMemberNames(typeof(IToolCatalogue)).OrderBy(name => name, StringComparer.Ordinal));
    }

    /// <summary>S14.5 — a registered tool whose parameter schema names a parameter matching the
    /// redaction marker set fails startup with <c>HostStartupError.SensitiveToolParameter</c>,
    /// naming the tool and the parameter.</summary>
    [Fact]
    public async Task A_tool_naming_a_sensitive_parameter_fails_startup_naming_the_tool_and_parameter()
    {
        var producer = new StubToolProducer(
            "producer",
            new ToolDefinition(
                new ToolName("leaky-tool"),
                "Names a sensitive parameter.",
                Schema("apiKey"),
                SamplePermission,
                null));

        var error = await RefusedAsync([producer], exposed: [new ToolName("leaky-tool")]);

        Assert.Equal(nameof(HostStartupError.SensitiveToolParameter), error.Code);
        Assert.Contains("leaky-tool", error.Detail, StringComparison.Ordinal);
        Assert.Contains("apiKey", error.Detail, StringComparison.Ordinal);
    }

    /// <summary>S14.6 — a tool requiring a permission no catalog declares fails startup with
    /// <c>HostStartupError.UnregisteredPermission</c>.</summary>
    [Fact]
    public async Task A_tool_requiring_an_undeclared_permission_fails_startup()
    {
        var producer = new StubToolProducer(
            "producer",
            new ToolDefinition(
                new ToolName("orphaned-tool"),
                "Requires a permission nothing declares.",
                Schema(),
                new PermissionName("Nothing.Declares.This"),
                null));

        var error = await RefusedAsync([producer], exposed: [new ToolName("orphaned-tool")], declareSamplePermission: false);

        Assert.Equal(nameof(HostStartupError.UnregisteredPermission), error.Code);
        Assert.Contains("orphaned-tool", error.Detail, StringComparison.Ordinal);
        Assert.Contains("Nothing.Declares.This", error.Detail, StringComparison.Ordinal);
    }

    /// <summary>S14.7 — each producer's production runs once at startup and never again, and the
    /// catalogue exposes no registration, unregistration or re-exposure member.</summary>
    [Fact]
    public async Task Each_producer_runs_exactly_once_and_the_catalogue_exposes_no_mutation_member()
    {
        var producer = new StubToolProducer(
            "producer",
            new ToolDefinition(new ToolName("counted-tool"), "Counted.", Schema(), SamplePermission, null));

        var (app, catalogue) = await StartWithProducersAsync([producer], exposed: [new ToolName("counted-tool")]);

        try
        {
            // Resolving the catalogue and reading it repeatedly must not re-run production.
            _ = catalogue.Exposed;
            _ = catalogue.TryGetExposed(new ToolName("counted-tool"), out _);

            Assert.Equal(1, producer.CallCount);

            // I-M1's structural half: the catalogue's public surface has exactly Exposed and
            // TryGetExposed — no Register, Unregister or re-Expose member exists to call.
            Assert.Equal(
                ["Exposed", "TryGetExposed"],
                DeclaredMemberNames(typeof(IToolCatalogue)).OrderBy(name => name, StringComparer.Ordinal));
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    /// <summary>Member names on <paramref name="type"/>, excluding compiler-generated property
    /// accessors — so a property counts once, under its own name, rather than once again as
    /// <c>get_Name</c>.</summary>
    private static IEnumerable<string> DeclaredMemberNames(Type type) =>
        type.GetMembers(BindingFlags.Public | BindingFlags.Instance)
            .Where(member => member is not MethodInfo method || !method.IsSpecialName)
            .Select(member => member.Name);

    private static JsonElement Schema(params string[] parameterNames)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "object");
            writer.WriteStartObject("properties");
            foreach (var name in parameterNames)
            {
                writer.WriteStartObject(name);
                writer.WriteString("type", "string");
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return JsonDocument.Parse(stream.ToArray()).RootElement;
    }

    private static async Task<(WebApplication App, IToolCatalogue Catalogue)> StartWithProducersAsync(
        IReadOnlyCollection<IToolProducer> producers,
        IReadOnlyCollection<ToolName> exposed)
    {
        var (app, _) = await WebHostUnderTest.StartAsync(services =>
        {
            services.AddSingleton<IPlatformModule, McpModule>();
            services.AddSingleton(new McpOptions { ExposedTools = exposed.ToHashSet() });
            foreach (var producer in producers)
            {
                services.AddSingleton<IToolProducer>(producer);
            }

            services.AddSingleton<IPermissionCatalog>(new StubPermissionCatalog(SamplePermission));
        });

        return (app, app.Services.GetRequiredService<IToolCatalogue>());
    }

    /// <summary>Starts a host expected to refuse during startup, and returns why.</summary>
    private static async Task<HostStartupError> RefusedAsync(
        IReadOnlyCollection<IToolProducer> producers,
        IReadOnlyCollection<ToolName> exposed,
        bool declareSamplePermission = true)
    {
        WebApplication? app = null;
        try
        {
            (app, _) = await WebHostUnderTest.StartAsync(services =>
            {
                services.AddSingleton<IPlatformModule, McpModule>();
                services.AddSingleton(new McpOptions { ExposedTools = exposed.ToHashSet() });
                foreach (var producer in producers)
                {
                    services.AddSingleton<IToolProducer>(producer);
                }

                if (declareSamplePermission)
                {
                    services.AddSingleton<IPermissionCatalog>(new StubPermissionCatalog(SamplePermission));
                }
            });
        }
        catch (PlatformStartupException exception)
        {
            return Assert.IsType<HostStartupError>(exception.Error);
        }
        finally
        {
            if (app is not null)
            {
                await app.DisposeAsync();
            }
        }

        throw new InvalidOperationException("The host started; it was expected to refuse.");
    }
}

/// <summary>A fixed, in-memory <see cref="IToolProducer"/> — stands in for both the manifest-
/// projecting and the product-owned fixed-table shape, since the contract requires the catalogue
/// treat them identically regardless of how a producer computed its definitions.</summary>
internal sealed class StubToolProducer(string name, params ToolDefinition[] definitions) : IToolProducer
{
    public ToolProducerName Name { get; } = new(name);

    public int CallCount { get; private set; }

    public ValueTask<IReadOnlyCollection<ToolDefinition>> ProduceAsync(CancellationToken cancellationToken)
    {
        CallCount++;
        return ValueTask.FromResult<IReadOnlyCollection<ToolDefinition>>(definitions);
    }
}
