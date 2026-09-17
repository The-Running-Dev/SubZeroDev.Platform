using System.Text.Json;

namespace SubZeroDev.Platform.Tests;

/// <summary>S17.5's nine deliberately broken observations. The ordinary scenario observes the
/// real result. A mutation run names exactly one capability through <c>PLATFORM_S17_MUTATION</c>
/// and flips that capability's negative criterion to false, so the scenario test must fail at the
/// same assertion that protects the real implementation.</summary>
internal static class S17MutationFixtures
{
    private const string EnvironmentVariable = "PLATFORM_S17_MUTATION";

    private static readonly IReadOnlyDictionary<string, S17MutationFixture> Fixtures = LoadFixtures();

    internal static IReadOnlyCollection<string> CapabilityNames => Fixtures.Keys.ToArray();

    internal static bool IsActive(string capability)
    {
        if (!Fixtures.ContainsKey(capability))
        {
            throw new InvalidOperationException($"No S17.5 mutation fixture is declared for '{capability}'.");
        }

        var active = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(active))
        {
            return false;
        }

        if (!Fixtures.ContainsKey(active))
        {
            throw new InvalidOperationException(
                $"Unknown {EnvironmentVariable} value '{active}'. Expected one of: {string.Join(", ", Fixtures.Keys)}.");
        }

        return string.Equals(active, capability, StringComparison.Ordinal);
    }

    internal static string FailureMessage(string capability)
    {
        var fixture = Fixtures[capability];
        return $"[S17.5:{capability}] mutation escaped: {fixture.Mutation}; negative criterion: {fixture.NegativeCriterion}.";
    }

    internal static string FindRepositoryRoot(string start)
    {
        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SubZeroDev.Platform.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException($"Could not locate the repository root above '{start}'.");
    }

    private static IReadOnlyDictionary<string, S17MutationFixture> LoadFixtures()
    {
        var root = FindRepositoryRoot(AppContext.BaseDirectory);
        var path = Path.Combine(root, "tests", "SubZeroDev.Platform.Tests", "S17MutationFixtures.json");
        var fixtures = JsonSerializer.Deserialize<List<S17MutationFixture>>(
            File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException($"'{path}' contained no S17.5 mutation fixtures.");

        return fixtures.ToDictionary(fixture => fixture.Capability, StringComparer.Ordinal);
    }

}

internal sealed record S17MutationFixture(string Capability, string NegativeCriterion, string Mutation);

public sealed class S17MutationFixtureDeclarationTests
{
    private static readonly string[] ExpectedCapabilities =
        ["Identity", "Authorization", "Organizations", "Tenancy", "Billing", "Licensing", "Audit", "SharedWebUi", "Mcp"];

    [Fact]
    public void S17_5_declares_exactly_one_negative_mutation_fixture_for_each_capability()
    {
        Assert.Equal(
            ExpectedCapabilities.Order(StringComparer.Ordinal),
            S17MutationFixtures.CapabilityNames.Order(StringComparer.Ordinal));
    }
}
