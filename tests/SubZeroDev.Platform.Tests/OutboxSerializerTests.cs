using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Persistence;
using SubZeroDev.Platform.Testing;

namespace SubZeroDev.Platform.Tests;

/// <summary>Platform's pinned <c>System.Text.Json</c> options: not injectable, no converter escape
/// hatch, and additive-payload compatible in both directions.</summary>
public sealed class OutboxSerializerTests
{
    [Fact]
    public void Unmapped_members_are_ignored_on_deserialize()
    {
        var deserialized = JsonSerializer.Deserialize<TestEvent>(
            """{"value":"hello","extra":"ignored"}""", OutboxSerializer.Options);

        Assert.Equal("hello", deserialized!.Value);
    }

    [Fact]
    public void Enums_serialize_as_their_declared_name()
    {
        var json = JsonSerializer.Serialize(OutboxMessageState.Poisoned, OutboxSerializer.Options);
        Assert.Equal("\"Poisoned\"", json);
    }

    [Fact]
    public void A_type_that_gained_an_optional_field_reads_rows_written_before_it_and_after_it()
    {
        var beforeUpgrade = JsonSerializer.Serialize(new OldShape("a"), OutboxSerializer.Options);
        var readByUpgraded = JsonSerializer.Deserialize<NewShape>(beforeUpgrade, OutboxSerializer.Options);
        Assert.Equal("a", readByUpgraded!.Value);
        Assert.Null(readByUpgraded.AddedLater);

        var afterUpgrade = JsonSerializer.Serialize(new NewShape("b", "extra"), OutboxSerializer.Options);
        var readByOld = JsonSerializer.Deserialize<OldShape>(afterUpgrade, OutboxSerializer.Options);
        Assert.Equal("b", readByOld!.Value);
    }

    [Fact]
    public async Task The_pinned_options_are_not_resolvable_from_the_container()
    {
        await using var host = await PlatformTestHost.CreateBuilder()
            .WithProvider(PersistenceProvider.Sqlite)
            .StartAsync(CancellationToken.None);

        Assert.Null(host.Services.GetService(typeof(JsonSerializerOptions)));
    }

    [Fact]
    public void No_public_member_in_persistence_accepts_a_converter_or_the_options_type()
    {
        var publicMethods = typeof(PlatformPersistenceExtensions).Assembly
            .GetExportedTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance));

        Assert.DoesNotContain(publicMethods, method => method.GetParameters().Any(parameter =>
            typeof(JsonConverter).IsAssignableFrom(parameter.ParameterType)
            || parameter.ParameterType == typeof(JsonSerializerOptions)));
    }

    private sealed record OldShape(string Value);

    private sealed record NewShape(string Value, string? AddedLater = null);
}
