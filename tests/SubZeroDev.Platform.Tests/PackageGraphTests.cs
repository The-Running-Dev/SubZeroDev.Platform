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
        // Resolved over every module built alongside this run, not the framework alone — over the
        // framework alone this direction has no module to check and passes vacuously. The fixture
        // test below proves the check itself would catch a real violation.
        var graph = PackageGraph.Resolve(AllPlatformAssemblies());

        var violations = PackageGraph.ModuleReferencesModule(graph);

        Assert.Empty(violations);
    }

    [Fact]
    public void S43_9_the_graph_holds_fourteen_packages_and_RuntimeSettings_references_only_three_and_is_referenced_by_none()
    {
        var graph = PackageGraph.Resolve(AllPlatformAssemblies());
        const string runtimeSettings = "SubZeroDev.Platform.RuntimeSettings";

        Assert.Equal(14, graph.Count);
        Assert.Equal(
            ["SubZeroDev.Platform.Abstractions", "SubZeroDev.Platform.Core", "SubZeroDev.Platform.Persistence"],
            graph[runtimeSettings].Order(StringComparer.Ordinal));
        Assert.DoesNotContain(graph, entry => entry.Value.Contains(runtimeSettings));
    }

    [Fact]
    public void S43_9_RuntimeSettings_implements_no_permission_provider_and_no_entitlement_contributor()
    {
        var types = typeof(SubZeroDev.Platform.RuntimeSettings.RuntimeSettingsModule).Assembly.GetTypes();

        Assert.DoesNotContain(types, type => typeof(IPermissionProvider).IsAssignableFrom(type));
        Assert.DoesNotContain(types, type => typeof(IEntitlementContributor).IsAssignableFrom(type));
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

    // S16.6 -------------------------------------------------------------------------------------

    /// <summary>I-W1's first half: no backend package references the shell. The shell is a
    /// front-end build with no .NET package at all (<c>design/20-contract.md</c> § <em>Types</em>
    /// 11) — checked here as no <c>.csproj</c> under <c>src/</c> or <c>samples/</c> naming
    /// <c>shell</c> in a project or package reference, and the shell's own directory carrying no
    /// <c>.csproj</c> for one to reference.</summary>
    [Fact]
    public void I_W1_no_dotnet_project_references_the_shell()
    {
        var repositoryRoot = FindRepositoryRoot(AppContext.BaseDirectory);

        var shellDirectory = Path.Combine(repositoryRoot, "shell");
        Assert.True(Directory.Exists(shellDirectory), $"'{shellDirectory}' does not exist.");
        Assert.Empty(Directory.GetFiles(shellDirectory, "*.csproj", SearchOption.AllDirectories));

        var projectFiles = Directory
            .GetFiles(Path.Combine(repositoryRoot, "src"), "*.csproj", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(Path.Combine(repositoryRoot, "samples"), "*.csproj", SearchOption.AllDirectories));

        foreach (var projectFile in projectFiles)
        {
            var content = File.ReadAllText(projectFile);
            Assert.DoesNotContain(
                "shell",
                content,
                StringComparison.OrdinalIgnoreCase);
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

    // S14.8 -------------------------------------------------------------------------------------

    /// <summary>I-M9's containment half: nothing outside Mcp references the SDK. S14 does not yet
    /// wire the SDK's transport in — that is S15's "the SDK transport and session" — so this proves
    /// the boundary the check exists to hold, ahead of anything crossing it.</summary>
    [Fact]
    public void I_M9_ModelContextProtocol_is_referenced_by_no_package_other_than_Mcp()
    {
        var graph = SdkReferenceGuard.Resolve(AllPlatformAssemblies());

        var violations = SdkReferenceGuard.ReferencedOutsideMcp(graph);

        Assert.Empty(violations);
    }

    [Fact]
    public void S14_8_the_reference_check_fails_against_a_deliberately_broken_fixture()
    {
        var broken = new Dictionary<string, IReadOnlySet<string>>
        {
            ["SubZeroDev.Platform.Organizations"] = new HashSet<string> { "ModelContextProtocol.Core" },
        };

        var violations = SdkReferenceGuard.ReferencedOutsideMcp(broken);

        Assert.Equal(["SubZeroDev.Platform.Organizations -> ModelContextProtocol.Core"], violations);
    }

    [Fact]
    public void I_M9_no_platform_public_type_exposes_returns_accepts_or_derives_from_an_sdk_type()
    {
        var map = SdkTypeSurfaceGuard.Resolve(AllPlatformAssemblies());

        var violations = SdkTypeSurfaceGuard.Violations(map);

        Assert.Empty(violations);
    }

    [Fact]
    public void S14_8_the_surface_check_fails_against_a_deliberately_broken_fixture()
    {
        var broken = new Dictionary<string, IReadOnlySet<string>>
        {
            ["SubZeroDev.Platform.Mcp.LeakyType"] = new HashSet<string> { "ModelContextProtocol.Protocol.Tool" },
        };

        var violations = SdkTypeSurfaceGuard.Violations(broken);

        Assert.Equal(["SubZeroDev.Platform.Mcp.LeakyType -> ModelContextProtocol.Protocol.Tool"], violations);
    }

    // S20.10 ------------------------------------------------------------------------------------

    private const string IdentityModelPrefix = "Microsoft.IdentityModel";

    /// <summary>I-I12's containment half: the token libraries are referenced by Identity and by
    /// no other Platform project.</summary>
    [Fact]
    public void I_I12_Microsoft_IdentityModel_is_referenced_by_no_package_other_than_Identity()
    {
        var graph = SdkReferenceGuard.Resolve(AllPlatformAssemblies(), IdentityModelPrefix);

        var violations = SdkReferenceGuard.ReferencedOutside(graph, "SubZeroDev.Platform.Identity");

        Assert.Empty(violations);
    }

    [Fact]
    public void S20_10_the_reference_check_fails_against_a_deliberately_broken_fixture()
    {
        var broken = new Dictionary<string, IReadOnlySet<string>>
        {
            ["SubZeroDev.Platform.Mcp"] = new HashSet<string> { "Microsoft.IdentityModel.JsonWebTokens" },
        };

        var violations = SdkReferenceGuard.ReferencedOutside(broken, "SubZeroDev.Platform.Identity");

        Assert.Equal(["SubZeroDev.Platform.Mcp -> Microsoft.IdentityModel.JsonWebTokens"], violations);
    }

    /// <summary>I-I12's surface half: no Platform public type exposes, returns, accepts or derives
    /// from an IdentityModel type — Identity's own public types included.</summary>
    [Fact]
    public void I_I12_no_platform_public_type_exposes_returns_accepts_or_derives_from_an_IdentityModel_type()
    {
        var map = SdkTypeSurfaceGuard.Resolve(AllPlatformAssemblies());

        var violations = SdkTypeSurfaceGuard.Violations(map, IdentityModelPrefix);

        Assert.Empty(violations);
    }

    [Fact]
    public void S20_10_the_surface_check_fails_against_a_deliberately_broken_fixture()
    {
        var broken = new Dictionary<string, IReadOnlySet<string>>
        {
            ["SubZeroDev.Platform.Identity.LeakyType"] = new HashSet<string> { "Microsoft.IdentityModel.Tokens.SecurityKey" },
        };

        var violations = SdkTypeSurfaceGuard.Violations(broken, IdentityModelPrefix);

        Assert.Equal(
            ["SubZeroDev.Platform.Identity.LeakyType -> Microsoft.IdentityModel.Tokens.SecurityKey"],
            violations);
    }

    // S23.2 ------------------------------------------------------------------------------------

    private const string VendorFixture = "Vendor.ConfigurationFixture";

    /// <summary>I-I13: a vendor configuration package references no Platform package. The compiler drops
    /// a reference nothing uses, so the project file is read as well as the built assembly.</summary>
    [Fact]
    public void I_I13_the_vendor_configuration_fixture_references_no_Platform_package()
    {
        var repositoryRoot = FindRepositoryRoot(AppContext.BaseDirectory);
        var project = Path.Combine(repositoryRoot, "tests", VendorFixture, $"{VendorFixture}.csproj");
        var assembly = typeof(Vendor.ConfigurationFixture.VendorConfigurationExtensions).Assembly;

        var graph = new Dictionary<string, IReadOnlySet<string>>
        {
            [$"{VendorFixture} (assembly)"] = ReferencedAssemblyNames(assembly.Location),
            [$"{VendorFixture} (project file)"] = VendorPackageGuard.ProjectFileReferences(File.ReadAllText(project)),
        };

        Assert.Empty(VendorPackageGuard.PlatformReferences(graph));
        Assert.Contains("Microsoft.Extensions.Configuration.Abstractions", graph[$"{VendorFixture} (project file)"]);
    }

    [Fact]
    public void S23_2_the_vendor_check_fails_against_a_deliberately_broken_fixture()
    {
        var brokenProject = """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Microsoft.Extensions.Configuration.Abstractions" Version="10.0.10" />
                <ProjectReference Include="..\..\src\SubZeroDev.Platform.Identity\SubZeroDev.Platform.Identity.csproj" />
                <PackageReference Include="SubZeroDev.Platform.Core" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """;
        var broken = new Dictionary<string, IReadOnlySet<string>>
        {
            ["Vendor.Broken"] = VendorPackageGuard.ProjectFileReferences(brokenProject),
        };

        var violations = VendorPackageGuard.PlatformReferences(broken);

        Assert.Equal(
            ["Vendor.Broken -> SubZeroDev.Platform.Core", "Vendor.Broken -> SubZeroDev.Platform.Identity"],
            violations.Order(StringComparer.Ordinal));
    }

    // S27.4 ------------------------------------------------------------------------------------

    /// <summary>I-O8, held on its own rather than through I-C6: a framework type could learn of an
    /// owner without referencing any module — a field, a parameter, a type of its own — and the
    /// package graph would never see it.</summary>
    [Fact]
    public void I_O8_no_framework_type_or_member_refers_to_a_tenants_owner()
    {
        var map = OwnerConceptGuard.Resolve(FrameworkAssemblies());

        var violations = OwnerConceptGuard.Violations(map);

        Assert.Empty(violations);
        Assert.Equal(FrameworkAssemblies().Count, map.Count);
        Assert.All(map.Values, identifiers => Assert.NotEmpty(identifiers));
    }

    [Fact]
    public void S27_4_the_owner_check_fails_against_a_deliberately_broken_fixture()
    {
        var broken = new Dictionary<string, IReadOnlySet<string>>
        {
            ["SubZeroDev.Platform.Core"] = new HashSet<string>
            {
                "SubZeroDev.Platform.Core.TenantId",
                "SubZeroDev.Platform.Core.TenantId.OwnerId",
                "SubZeroDev.Platform.Core.TenantDirectory.Resolve(organization)",
                "SubZeroDev.Platform.Organizations.OrganizationId",
                "SubZeroDev.Platform.Abstractions.PlatformPermissions.get_AdministerOrganization",
                "SubZeroDev.Platform.Abstractions.PlatformPermissions.AdministerOrganizationOwner",
            },
        };

        var violations = OwnerConceptGuard.Violations(broken);

        Assert.Equal(
            [
                "SubZeroDev.Platform.Core -> SubZeroDev.Platform.Abstractions.PlatformPermissions.AdministerOrganizationOwner",
                "SubZeroDev.Platform.Core -> SubZeroDev.Platform.Core.TenantDirectory.Resolve(organization)",
                "SubZeroDev.Platform.Core -> SubZeroDev.Platform.Core.TenantId.OwnerId",
                "SubZeroDev.Platform.Core -> SubZeroDev.Platform.Organizations.OrganizationId",
            ],
            violations.Order(StringComparer.Ordinal));
    }

    /// <summary>Every assembly outside Billing that is built alongside this test run: the six
    /// framework assemblies plus every other module. Billing itself is deliberately absent — I-C8
    /// bounds what may reference its types from <em>outside</em> it, not from within.</summary>
    private static IReadOnlyCollection<Assembly> NonBillingAssemblies() =>
    [
        .. FrameworkAssemblies(),
        typeof(SubZeroDev.Platform.Identity.IdentityModule).Assembly,
        typeof(SubZeroDev.Platform.Organizations.Organization).Assembly,
        typeof(SubZeroDev.Platform.Licensing.LicensingModule).Assembly,
        typeof(SubZeroDev.Platform.Audit.AuditModule).Assembly,
        typeof(SubZeroDev.Platform.Mcp.McpModule).Assembly,
        typeof(SubZeroDev.Platform.SignIn.SignInModule).Assembly,
        typeof(SubZeroDev.Platform.RuntimeSettings.RuntimeSettingsModule).Assembly,
    ];

    /// <summary>Every framework assembly plus every module assembly built alongside this test
    /// run — I-M9 bounds the SDK's reach across the whole tree, not just inside Mcp.</summary>
    private static IReadOnlyCollection<Assembly> AllPlatformAssemblies() =>
    [
        .. FrameworkAssemblies(),
        typeof(SubZeroDev.Platform.Identity.IdentityModule).Assembly,
        typeof(SubZeroDev.Platform.Organizations.Organization).Assembly,
        typeof(SubZeroDev.Platform.Billing.BillingModule).Assembly,
        typeof(SubZeroDev.Platform.Licensing.LicensingModule).Assembly,
        typeof(SubZeroDev.Platform.Audit.AuditModule).Assembly,
        typeof(SubZeroDev.Platform.Mcp.McpModule).Assembly,
        typeof(SubZeroDev.Platform.SignIn.SignInModule).Assembly,
        typeof(SubZeroDev.Platform.RuntimeSettings.RuntimeSettingsModule).Assembly,
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
    /// name.</summary>
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

/// <summary>I-M9's package-reference half: <c>ModelContextProtocol.*</c> is referenced by
/// <c>SubZeroDev.Platform.Mcp</c> and by nothing else. Same abstracted-graph mechanism as
/// <see cref="PackageGraph"/>, filtered to the SDK's own assembly-name prefix rather than
/// <c>SubZeroDev.Platform.*</c>.</summary>
internal static class SdkReferenceGuard
{
    private const string SdkAssemblyPrefix = "ModelContextProtocol";

    /// <summary>Resolves the reference graph over a set of loaded assemblies, keeping only
    /// references whose name starts with <see cref="SdkAssemblyPrefix"/>.</summary>
    internal static IReadOnlyDictionary<string, IReadOnlySet<string>> Resolve(
        IReadOnlyCollection<Assembly> assemblies,
        string assemblyPrefix = SdkAssemblyPrefix)
    {
        var graph = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);

        foreach (var assembly in assemblies)
        {
            var references = assembly.GetReferencedAssemblies()
                .Select(referenced => referenced.Name)
                .Where(referencedName => referencedName is not null
                    && referencedName.StartsWith(assemblyPrefix, StringComparison.Ordinal))
                .Cast<string>()
                .ToHashSet(StringComparer.Ordinal);

            graph[assembly.GetName().Name!] = references;
        }

        return graph;
    }

    /// <summary>I-M9: nothing but <c>SubZeroDev.Platform.Mcp</c> may reference the SDK.</summary>
    internal static IReadOnlyList<string> ReferencedOutsideMcp(
        IReadOnlyDictionary<string, IReadOnlySet<string>> graph) =>
        ReferencedOutside(graph, "SubZeroDev.Platform.Mcp");

    /// <summary>The same containment, for any package that owns a dependency: nothing but
    /// <paramref name="owner"/> may reference it. I-I12 reuses it for Identity.</summary>
    internal static IReadOnlyList<string> ReferencedOutside(
        IReadOnlyDictionary<string, IReadOnlySet<string>> graph,
        string owner)
    {
        var violations = new List<string>();

        foreach (var (package, references) in graph)
        {
            if (package == owner)
            {
                continue;
            }

            violations.AddRange(references.Select(reference => $"{package} -> {reference}"));
        }

        return violations;
    }
}

/// <summary>I-M9's public-surface half: no Platform public type exposes, returns, accepts or
/// derives from an SDK type. On the same two-layer shape as <see cref="SubscriptionTypeGuard"/> —
/// <see cref="Resolve"/> does the real reflection, <see cref="Violations"/> is pure logic proved
/// first against a fixture no real build could produce.</summary>
internal static class SdkTypeSurfaceGuard
{
    private const string SdkNamespacePrefix = "ModelContextProtocol";

    /// <summary>Maps every public type's full name to the full names of every type its public
    /// surface — base type, interfaces, and public property, field and method signatures — refers
    /// to, generic arguments and array element types flattened in.</summary>
    internal static IReadOnlyDictionary<string, IReadOnlySet<string>> Resolve(IReadOnlyCollection<Assembly> assemblies)
    {
        var map = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);

        foreach (var assembly in assemblies)
        {
            foreach (var type in assembly.GetExportedTypes())
            {
                var referenced = SurfaceTypeNames(type);
                if (referenced.Count > 0)
                {
                    map[type.FullName ?? type.Name] = referenced;
                }
            }
        }

        return map;
    }

    /// <summary>I-M9: nothing in <paramref name="typeReferencesByType"/> may name an SDK type.</summary>
    internal static IReadOnlyList<string> Violations(
        IReadOnlyDictionary<string, IReadOnlySet<string>> typeReferencesByType,
        string namespacePrefix = SdkNamespacePrefix)
    {
        var violations = new List<string>();

        foreach (var (type, referenced) in typeReferencesByType)
        {
            violations.AddRange(
                referenced
                    .Where(name => name.StartsWith(namespacePrefix, StringComparison.Ordinal))
                    .Select(name => $"{type} -> {name}"));
        }

        return violations;
    }

    private static IReadOnlySet<string> SurfaceTypeNames(Type type)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        void Add(Type candidate)
        {
            foreach (var flattened in Flatten(candidate))
            {
                if (flattened.FullName is { } fullName)
                {
                    names.Add(fullName);
                }
            }
        }

        if (type.BaseType is not null)
        {
            Add(type.BaseType);
        }

        foreach (var implemented in type.GetInterfaces())
        {
            Add(implemented);
        }

        foreach (var member in type.GetMembers(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            switch (member)
            {
                case PropertyInfo property:
                    Add(property.PropertyType);
                    break;
                case FieldInfo field:
                    Add(field.FieldType);
                    break;
                case MethodInfo method when !method.IsSpecialName:
                    Add(method.ReturnType);
                    foreach (var parameter in method.GetParameters())
                    {
                        Add(parameter.ParameterType);
                    }

                    break;
            }
        }

        return names;
    }

    /// <summary>Yields <paramref name="type"/> itself, then every generic argument and array
    /// element type it carries, recursively — so <c>Task&lt;IReadOnlyCollection&lt;Tool&gt;&gt;</c>
    /// surfaces <c>Tool</c> rather than hiding it inside the wrapper types.</summary>
    private static IEnumerable<Type> Flatten(Type type)
    {
        yield return type;

        if (type.IsGenericType)
        {
            foreach (var argument in type.GetGenericArguments())
            {
                foreach (var nested in Flatten(argument))
                {
                    yield return nested;
                }
            }
        }

        if (type.HasElementType)
        {
            foreach (var nested in Flatten(type.GetElementType()!))
            {
                yield return nested;
            }
        }
    }
}

/// <summary>The checking logic behind I-I13, over an abstracted reference graph so the same function is
/// proved against a broken fixture before it is trusted against the real one (S23.2).</summary>
internal static class VendorPackageGuard
{
    private const string PlatformPrefix = "SubZeroDev.Platform.";

    private static readonly System.Text.RegularExpressions.Regex ReferenceItem = new(
        @"<(?:ProjectReference|PackageReference)\s[^>]*Include=""([^""]+)""",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>The names a project file's <c>ProjectReference</c> and <c>PackageReference</c> items
    /// include, with the path and <c>.csproj</c> extension stripped from a project reference.</summary>
    internal static IReadOnlySet<string> ProjectFileReferences(string projectXml) =>
        ReferenceItem.Matches(projectXml)
            .Select(match => match.Groups[1].Value.Replace('\\', '/'))
            .Select(include => include[(include.LastIndexOf('/') + 1)..])
            .Select(name => name.EndsWith(".csproj", StringComparison.Ordinal) ? name[..^".csproj".Length] : name)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>I-I13: a vendor package may not reference any <c>SubZeroDev.Platform.*</c> package.</summary>
    internal static IReadOnlyList<string> PlatformReferences(
        IReadOnlyDictionary<string, IReadOnlySet<string>> graph) =>
        [
            .. graph.SelectMany(entry => entry.Value
                .Where(reference => reference.StartsWith(PlatformPrefix, StringComparison.Ordinal))
                .Select(reference => $"{entry.Key} -> {reference}")),
        ];
}

/// <summary>I-O8's checking logic: every identifier a framework assembly declares or references —
/// type, field, property, event, method and parameter names, and the types and members it
/// references from elsewhere — read from the compiled metadata, and none may name an owner. The
/// owner words are the ones the contract gives the concept: an owner, and the organization that is
/// one (<c>design/20-contract.md</c>, "An organization holds a tenant; it is not one").</summary>
internal static class OwnerConceptGuard
{
    internal static readonly IReadOnlyCollection<string> OwnerWords = ["Owner", "Organization"];

    /// <summary>The one name that carries an owner word without the concept: the permission
    /// Organizations checks, which the framework's permission catalogue names so that a consumer's
    /// policy can refer to it (<c>PlatformPermissions</c>). It is a permission's name, not a
    /// relation — nothing in it says which tenant an organization holds. Removed from an identifier
    /// before the words are looked for, so the same identifier carrying a second owner word still
    /// fails.</summary>
    internal static readonly IReadOnlyCollection<string> ExemptNames = ["AdministerOrganization"];

    /// <summary>Reads each assembly's <c>TypeDef</c>, member, parameter, <c>TypeRef</c> and
    /// <c>MemberRef</c> tables — never loading or running it, on the discipline
    /// <see cref="SubscriptionTypeGuard"/> uses.</summary>
    internal static IReadOnlyDictionary<string, IReadOnlySet<string>> Resolve(IReadOnlyCollection<Assembly> assemblies)
    {
        var map = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);

        foreach (var assembly in assemblies)
        {
            using var stream = File.OpenRead(assembly.Location);
            using var peReader = new PEReader(stream);
            var reader = peReader.GetMetadataReader();

            var identifiers = new HashSet<string>(StringComparer.Ordinal);
            foreach (var handle in reader.TypeDefinitions)
            {
                var type = reader.GetTypeDefinition(handle);
                var typeName = Qualified(reader, type.Namespace, type.Name);
                identifiers.Add(typeName);

                foreach (var field in type.GetFields())
                {
                    identifiers.Add($"{typeName}.{reader.GetString(reader.GetFieldDefinition(field).Name)}");
                }

                foreach (var property in type.GetProperties())
                {
                    identifiers.Add($"{typeName}.{reader.GetString(reader.GetPropertyDefinition(property).Name)}");
                }

                foreach (var @event in type.GetEvents())
                {
                    identifiers.Add($"{typeName}.{reader.GetString(reader.GetEventDefinition(@event).Name)}");
                }

                foreach (var methodHandle in type.GetMethods())
                {
                    var method = reader.GetMethodDefinition(methodHandle);
                    var methodName = $"{typeName}.{reader.GetString(method.Name)}";
                    identifiers.Add(methodName);

                    foreach (var parameter in method.GetParameters())
                    {
                        var name = reader.GetString(reader.GetParameter(parameter).Name);
                        if (name.Length > 0)
                        {
                            identifiers.Add($"{methodName}({name})");
                        }
                    }
                }
            }

            foreach (var handle in reader.TypeReferences)
            {
                var reference = reader.GetTypeReference(handle);
                identifiers.Add(Qualified(reader, reference.Namespace, reference.Name));
            }

            foreach (var handle in reader.MemberReferences)
            {
                identifiers.Add(reader.GetString(reader.GetMemberReference(handle).Name));
            }

            map[assembly.GetName().Name!] = identifiers;
        }

        return map;
    }

    /// <summary>I-O8: no identifier in <paramref name="identifiersByAssembly"/> contains an owner
    /// word, compared ignoring case so <c>owner</c> as a parameter is caught as surely as
    /// <c>OwnerId</c> as a type.</summary>
    internal static IReadOnlyList<string> Violations(
        IReadOnlyDictionary<string, IReadOnlySet<string>> identifiersByAssembly)
    {
        var violations = new List<string>();

        foreach (var (assembly, identifiers) in identifiersByAssembly)
        {
            violations.AddRange(
                identifiers
                    .Where(identifier => OwnerWords.Any(
                        word => WithoutExemptNames(identifier).Contains(word, StringComparison.OrdinalIgnoreCase)))
                    .Select(identifier => $"{assembly} -> {identifier}"));
        }

        return violations;
    }

    private static string WithoutExemptNames(string identifier) =>
        ExemptNames.Aggregate(identifier, (remaining, exempt) => remaining.Replace(exempt, string.Empty, StringComparison.Ordinal));

    private static string Qualified(MetadataReader reader, StringHandle @namespace, StringHandle name)
    {
        var namespaceText = reader.GetString(@namespace);
        return namespaceText.Length == 0 ? reader.GetString(name) : $"{namespaceText}.{reader.GetString(name)}";
    }
}
