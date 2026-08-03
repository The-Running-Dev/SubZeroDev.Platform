using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;

namespace SubZeroDev.Platform.Tests;

public sealed class ModuleGraphTests
{
    [Fact]
    public void Resolve_orders_dependencies_first_and_breaks_ties_by_name()
    {
        IReadOnlyCollection<IPlatformModule> modules =
        [
            new StubModule("B", "A"),
            new StubModule("C"),
            new StubModule("A"),
        ];

        var resolved = new ModuleRegistry().Resolve(modules);

        Assert.True(resolved.IsSuccess);
        Assert.Equal(["A", "B", "C"], resolved.Value.Select(module => module.Name.Value));
    }

    [Fact]
    public void Resolve_returns_the_same_order_whatever_the_discovery_order()
    {
        var orders = new[]
        {
            new IPlatformModule[] { new StubModule("B", "A"), new StubModule("C"), new StubModule("A") },
            new IPlatformModule[] { new StubModule("A"), new StubModule("B", "A"), new StubModule("C") },
            new IPlatformModule[] { new StubModule("C"), new StubModule("A"), new StubModule("B", "A") },
        };

        var results = orders
            .Select(order => new ModuleRegistry().Resolve(order))
            .Select(resolved => string.Join(",", resolved.Value.Select(module => module.Name.Value)))
            .Distinct()
            .ToList();

        Assert.Equal(["A,B,C"], results);
    }

    [Fact]
    public void A_dependency_no_module_provides_is_named()
    {
        var resolved = new ModuleRegistry().Resolve([new StubModule("Orders", "Invoices")]);

        Assert.False(resolved.IsSuccess);
        Assert.Equal("MissingDependency", resolved.Error.Code);
        Assert.Contains("Orders", resolved.Error.Detail, StringComparison.Ordinal);
        Assert.Contains("Invoices", resolved.Error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_modules_of_one_name_are_rejected()
    {
        var resolved = new ModuleRegistry().Resolve([new StubModule("Orders"), new StubModule("Orders")]);

        Assert.False(resolved.IsSuccess);
        Assert.Equal("DuplicateModuleName", resolved.Error.Code);
        Assert.Contains("Orders", resolved.Error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cycle_is_named_rather_than_hanging()
    {
        var resolved = new ModuleRegistry().Resolve([new StubModule("A", "B"), new StubModule("B", "A")]);

        Assert.False(resolved.IsSuccess);
        Assert.Equal("CyclicDependency", resolved.Error.Code);
        Assert.Contains("A", resolved.Error.Detail, StringComparison.Ordinal);
        Assert.Contains("B", resolved.Error.Detail, StringComparison.Ordinal);
    }
}
