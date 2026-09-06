using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Hosting;
using SubZeroDev.Platform.Observability;
using SubZeroDev.Platform.Persistence;
using SubZeroDev.Platform.Testing;

namespace SubZeroDev.Platform.Tests;

/// <summary>D5-S1.3 to S1.6: ADR-006 rules 1 and 2, held structurally rather than by review.
/// <see cref="PackageGraph"/> is the checking logic, proved first against a deliberately broken
/// fixture (S1.5) before it is trusted against the real, resolved package graph.</summary>
public sealed class PackageGraphTests
{
    [Fact]
    public void I_C6_no_framework_package_references_anything_outside_the_six()
    {
        var graph = PackageGraph.Resolve(FrameworkAssemblies());

        var violations = PackageGraph.FrameworkReferencesOutsideSix(graph);

        Assert.Empty(violations);
    }

    [Fact]
    public void I_C7_no_module_package_references_another_module_package()
    {
        // With no module package present today, this direction is vacuously satisfied — the
        // fixture test below is what proves the check itself would catch a real violation.
        var graph = PackageGraph.Resolve(FrameworkAssemblies());

        var violations = PackageGraph.ModuleReferencesModule(graph);

        Assert.Empty(violations);
    }

    [Fact]
    public void S1_5_the_first_direction_fails_against_a_deliberately_broken_graph()
    {
        var broken = new Dictionary<string, IReadOnlySet<string>>
        {
            ["SubZeroDev.Platform.Core"] = new HashSet<string> { "SubZeroDev.Platform.Identity" },
        };

        var violations = PackageGraph.FrameworkReferencesOutsideSix(broken);

        Assert.Equal(["SubZeroDev.Platform.Core -> SubZeroDev.Platform.Identity"], violations);
    }

    [Fact]
    public void S1_5_the_second_direction_fails_against_a_deliberately_broken_graph()
    {
        var broken = new Dictionary<string, IReadOnlySet<string>>
        {
            ["SubZeroDev.Platform.Identity"] = new HashSet<string> { "SubZeroDev.Platform.Organizations" },
        };

        var violations = PackageGraph.ModuleReferencesModule(broken);

        Assert.Equal(["SubZeroDev.Platform.Identity -> SubZeroDev.Platform.Organizations"], violations);
    }

    [Fact]
    public void I_C5_the_local_sample_has_zero_references_to_any_commercial_module_by_name()
    {
        var path = SampleAssemblyPath("SubZeroDev.Platform.Sample.Local");
        Assert.True(
            File.Exists(path),
            $"'{path}' does not exist — build the solution (which builds the local sample) before running this test.");

        var referenced = ReferencedAssemblyNames(path);

        foreach (var forbidden in PackageGraph.CommercialModules)
        {
            Assert.DoesNotContain(forbidden, referenced);
        }
    }

    // S11.3 -------------------------------------------------------------------------------------

    [Fact]
    public void I_C8_nothing_outside_Billing_references_SubscriptionState_or_any_subscription_type()
    {
        var map = SubscriptionTypeGuard.Resolve(NonBillingAssemblies());

        var violations = SubscriptionTypeGuard.Violations(map);

        Assert.Empty(violations);
    }

    [Fact]
    public void S11_3_the_check_fails_against_a_deliberately_broken_fixture()
    {
        var broken = new Dictionary<string, IReadOnlySet<string>>
        {
            ["SubZeroDev.Platform.Organizations"] = new HashSet<string> { "SubscriptionState" },
        };

        var violations = SubscriptionTypeGuard.Violations(broken);

        Assert.Equal(["SubZeroDev.Platform.Organizations -> SubscriptionState"], violations);
    }

    /// <summary>Every assembly outside Billing that is built alongside this test run: the six
    /// framework assemblies plus every other module. Billing itself is deliberately absent — I-C8
    /// bounds what may reference its types from <em>outside</em> it, not from within.</summary>
    private static IReadOnlyCollection<Assembly> NonBillingAssemblies() =>
    [
        .. FrameworkAssemblies(),
        typeof(SubZeroDev.Platform.Organizations.Organization).Assembly, // Organizations
    ];

    private static IReadOnlyCollection<Assembly> FrameworkAssemblies() =>
    [
        typeof(CompositionProfile).Assembly, // Abstractions
        typeof(PlatformOptions).Assembly, // Core
        typeof(PlatformHostExtensions).Assembly, // Hosting
        typeof(ITenantOwned).Assembly, // Persistence
        typeof(PlatformObservabilityExtensions).Assembly, // Observability
        typeof(FakeClock).Assembly, // Testing
    ];

    /// <summary>Reads the <c>AssemblyRef</c> table directly from the compiled metadata rather than
    /// loading the assembly, so inspecting a sample's dependency graph never runs its code or drags
    /// its runtime dependencies into this test process.</summary>
    private static IReadOnlySet<string> ReferencedAssemblyNames(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        var metadataReader = peReader.GetMetadataReader();

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var handle in metadataReader.AssemblyReferences)
        {
            var reference = metadataReader.GetAssemblyReference(handle);
            names.Add(metadataReader.GetString(reference.Name));
        }

        return names;
    }

    private static string SampleAssemblyPath(string sampleAssemblyName)
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var repositoryRoot = FindRepositoryRoot(AppContext.BaseDirectory);

        return Path.Combine(
            repositoryRoot,
            "samples",
            sampleAssemblyName,
            "bin",
            configuration,
            "net10.0",
            $"{sampleAssemblyName}.dll");
    }

    private static string FindRepositoryRoot(string start)
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
}

/// <summary>The checking logic behind I-C6 and I-C7, over an abstracted package-reference graph
/// (package name to the <c>SubZeroDev.Platform.*</c> package names it references) rather than over
/// live assemblies directly — which is what lets <see cref="PackageGraphTests"/> prove the same
/// two functions against a graph no real build can produce, per S1.5.</summary>
internal static class PackageGraph
{
    /// <summary>The six framework packages minimal-platform-packages.md §2 names. Anything
    /// <c>SubZeroDev.Platform.*</c> outside this set is a module.</summary>
    internal static readonly IReadOnlyCollection<string> FrameworkPackages =
    [
        "SubZeroDev.Platform.Abstractions",
        "SubZeroDev.Platform.Core",
        "SubZeroDev.Platform.Hosting",
        "SubZeroDev.Platform.Persistence",
        "SubZeroDev.Platform.Observability",
        "SubZeroDev.Platform.Testing",
    ];

    /// <summary>The four commercial modules I-C5 asserts the local sample never references by
    /// name. None exists in the tree yet; this list is what makes the assertion bite once one does.</summary>
    internal static readonly IReadOnlyCollection<string> CommercialModules =
    [
        "SubZeroDev.Platform.Identity",
        "SubZeroDev.Platform.Organizations",
        "SubZeroDev.Platform.Billing",
        "SubZeroDev.Platform.Licensing",
    ];

    /// <summary>Resolves the reference graph over a set of loaded assemblies, keeping only
    /// <c>SubZeroDev.Platform.*</c> references — the graph this check cares about.</summary>
    internal static IReadOnlyDictionary<string, IReadOnlySet<string>> Resolve(IReadOnlyCollection<Assembly> assemblies)
    {
        var graph = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);

        foreach (var assembly in assemblies)
        {
            var name = assembly.GetName().Name!;
            var references = assembly.GetReferencedAssemblies()
                .Select(referenced => referenced.Name)
                .Where(referencedName => referencedName is not null
                    && referencedName.StartsWith("SubZeroDev.Platform.", StringComparison.Ordinal))
                .Cast<string>()
                .ToHashSet(StringComparer.Ordinal);

            graph[name] = references;
        }

        return graph;
    }

    /// <summary>I-C6: a framework package may reference only another framework package.</summary>
    internal static IReadOnlyList<string> FrameworkReferencesOutsideSix(
        IReadOnlyDictionary<string, IReadOnlySet<string>> graph)
    {
        var violations = new List<string>();

        foreach (var package in FrameworkPackages)
        {
            if (!graph.TryGetValue(package, out var references))
            {
                continue;
            }

            violations.AddRange(
                references
                    .Where(reference => !FrameworkPackages.Contains(reference))
                    .Select(reference => $"{package} -> {reference}"));
        }

        return violations;
    }

    /// <summary>I-C7: a module package (anything in the graph outside the six) may reference only
    /// a framework package, never another module.</summary>
    internal static IReadOnlyList<string> ModuleReferencesModule(
        IReadOnlyDictionary<string, IReadOnlySet<string>> graph)
    {
        var violations = new List<string>();

        foreach (var (package, references) in graph)
        {
            if (FrameworkPackages.Contains(package))
            {
                continue;
            }

            violations.AddRange(
                references
                    .Where(reference => !FrameworkPackages.Contains(reference))
                    .Select(reference => $"{package} -> {reference}"));
        }

        return violations;
    }
}

/// <summary>I-C8's checking logic, on the same abstracted-graph mechanism <see cref="PackageGraph"/>
/// uses for I-C6 and I-C7 (S1) — a map from assembly name to the type names it references, checked
/// against a fixed forbidden set, proved first against a deliberately broken fixture (S11.3) before
/// it is trusted against the real, compiled assemblies.</summary>
internal static class SubscriptionTypeGuard
{
    /// <summary>The subscription-specific type names nothing outside Billing may reference (I-C8).
    /// Deliberately narrower than every type Billing declares: <c>PlanKey</c> and
    /// <c>ProviderEventReceipt</c> are not subscription types, and the invariant does not reach
    /// them.</summary>
    internal static readonly IReadOnlyCollection<string> SubscriptionTypeNames =
    [
        "SubscriptionState",
        "SubscriptionId",
        "Subscription",
    ];

    /// <summary>Reads the <c>TypeRef</c> table directly from each assembly's compiled metadata,
    /// keeping only the bare type names — the same "inspect metadata, never load or run the
    /// assembly" discipline <c>PackageGraphTests.ReferencedAssemblyNames</c> uses for assembly
    /// references.</summary>
    internal static IReadOnlyDictionary<string, IReadOnlySet<string>> Resolve(IReadOnlyCollection<Assembly> assemblies)
    {
        var map = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);

        foreach (var assembly in assemblies)
        {
            using var stream = File.OpenRead(assembly.Location);
            using var peReader = new PEReader(stream);
            var metadataReader = peReader.GetMetadataReader();

            var typeNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var handle in metadataReader.TypeReferences)
            {
                var reference = metadataReader.GetTypeReference(handle);
                typeNames.Add(metadataReader.GetString(reference.Name));
            }

            map[assembly.GetName().Name!] = typeNames;
        }

        return map;
    }

    /// <summary>I-C8: nothing in <paramref name="typeReferencesByAssembly"/> — which the caller
    /// builds from every assembly <em>outside</em> Billing — may reference a subscription type.</summary>
    internal static IReadOnlyList<string> Violations(
        IReadOnlyDictionary<string, IReadOnlySet<string>> typeReferencesByAssembly)
    {
        var violations = new List<string>();

        foreach (var (assembly, typeNames) in typeReferencesByAssembly)
        {
            violations.AddRange(
                typeNames
                    .Where(SubscriptionTypeNames.Contains)
                    .Select(typeName => $"{assembly} -> {typeName}"));
        }

        return violations;
    }
}
