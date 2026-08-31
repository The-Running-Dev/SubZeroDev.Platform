using System.Reflection;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Persistence;
using SubZeroDev.Platform.Testing;

namespace SubZeroDev.Platform.Tests;

public sealed class PrincipalTests
{
    /// <summary>S2.1: with no authentication anywhere in the composition, a request the local host
    /// originates observes a principal whose kind is <c>System</c> and whose id renders
    /// <c>system:local</c>.</summary>
    [Fact]
    public void LocalSystem_is_the_System_kind_and_renders_system_local()
    {
        Assert.Equal(PrincipalKind.System, Principal.LocalSystem.Kind);
        Assert.Equal("system:local", Principal.LocalSystem.Id.ToString());
        Assert.Equal("system:local", PrincipalId.LocalSystem.ToString());
    }

    /// <summary>S2.2: a request arriving with no credential observes a principal whose kind is
    /// <c>Anonymous</c>, and that value is distinct from <c>LocalSystem</c>'s.</summary>
    [Fact]
    public void Anonymous_is_the_Anonymous_kind_and_distinct_from_LocalSystem()
    {
        Assert.Equal(PrincipalKind.Anonymous, Principal.Anonymous.Kind);
        Assert.NotEqual(Principal.LocalSystem.Id, Principal.Anonymous.Id);
        Assert.NotEqual(Principal.LocalSystem, Principal.Anonymous);
    }

    /// <summary>S2.4: neither <c>IOperationScopeFactory.Begin</c> overload declares a default for its
    /// principal parameter — asserted by reflection over the parameter's <c>HasDefaultValue</c>.</summary>
    [Fact]
    public void Neither_Begin_overload_defaults_its_principal_parameter()
    {
        var overloads = typeof(IOperationScopeFactory).GetMethods().Where(m => m.Name == nameof(IOperationScopeFactory.Begin)).ToList();
        Assert.Equal(2, overloads.Count);

        foreach (var overload in overloads)
        {
            var principal = overload.GetParameters().Single(p => p.Name == "principal");
            Assert.Equal(typeof(Principal), principal.ParameterType);
            Assert.False(principal.HasDefaultValue);
        }
    }

    /// <summary>S2.6: <c>ModifiedBy</c> and <c>DeletedBy</c> remain nullable while <c>CreatedBy</c>
    /// becomes non-null, per the Unresolved #1 decision taken for S2 — asserted by reflection over
    /// nullable reference type metadata rather than assumed from the source.</summary>
    [Fact]
    public void CreatedBy_is_non_null_while_ModifiedBy_and_DeletedBy_stay_nullable()
    {
        var context = new NullabilityInfoContext();

        var createdBy = typeof(IAuditable).GetProperty(nameof(IAuditable.CreatedBy))!;
        Assert.Equal(NullabilityState.NotNull, context.Create(createdBy).ReadState);

        var modifiedBy = typeof(IAuditable).GetProperty(nameof(IAuditable.ModifiedBy))!;
        Assert.Equal(NullabilityState.Nullable, context.Create(modifiedBy).ReadState);

        var deletedBy = typeof(ISoftDeletable).GetProperty(nameof(ISoftDeletable.DeletedBy))!;
        Assert.Equal(NullabilityState.Nullable, context.Create(deletedBy).ReadState);
    }

    /// <summary>S2.7: two principals whose subjects are equal and whose issuers differ compare as
    /// unequal, and neither half is trimmed, lower-cased or otherwise normalised across a round
    /// trip.</summary>
    [Fact]
    public void PrincipalId_compares_both_halves_ordinally_with_no_normalisation()
    {
        var first = new PrincipalId("Issuer-A", "shared-subject");
        var second = new PrincipalId("Issuer-B", "shared-subject");
        Assert.NotEqual(first, second);

        var mixedCase = new PrincipalId("Mixed-CASE-Issuer", "  Padded-Subject  ");
        Assert.Equal("Mixed-CASE-Issuer", mixedCase.Issuer);
        Assert.Equal("  Padded-Subject  ", mixedCase.Subject);
        Assert.Equal("Mixed-CASE-Issuer:  Padded-Subject  ", mixedCase.ToString());
    }

    /// <summary>S2.8: <c>Testing</c> produces a fake principal of each of the four kinds, and its
    /// current-principal fake is non-null.</summary>
    [Fact]
    public void Testing_produces_a_fake_principal_of_each_kind_and_a_non_null_current_principal_fake()
    {
        Assert.Equal(PrincipalKind.Anonymous, FakePrincipals.Anonymous.Kind);
        Assert.Equal(PrincipalKind.System, FakePrincipals.System.Kind);
        Assert.Equal(PrincipalKind.Account, FakePrincipals.Account().Kind);
        Assert.Equal(PrincipalKind.Delegated, FakePrincipals.Delegated().Kind);

        var fake = new FakeCurrentPrincipal();
        Assert.NotNull(fake.Current);
        Assert.Equal(Principal.Anonymous, fake.Current);
    }
}
