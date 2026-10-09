using System.Runtime.CompilerServices;
using SubZeroDev.Platform.Abstractions;

namespace SubZeroDev.Platform.Identity;

/// <summary>The unlinked principals <see cref="AccountPrincipalMapping"/> passed through from a mapped
/// provider, held by reference and weakly. It is how <see cref="AccountApi"/> knows an Identity
/// provider established the ambient principal without reading its claims (I-I6): a principal built
/// anywhere else, even one equal by value, is not in it.</summary>
internal sealed class EstablishedPrincipals
{
    private static readonly object Marker = new();
    private readonly ConditionalWeakTable<Principal, object> _established = [];

    internal void Add(Principal principal) => _established.AddOrUpdate(principal, Marker);

    internal bool Contains(Principal principal) => _established.TryGetValue(principal, out _);
}
