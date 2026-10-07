using System.Text.RegularExpressions;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Testing;

namespace SubZeroDev.Platform.Tests;

/// <summary>#92: permissions are never carried in the login token. Platform's providers cannot read a
/// claim (I-I6), and the harness a consumer runs against its own provider fails one that carries a grant
/// between requests or reads it from the token (I-A10).</summary>
public sealed class PermissionGrantSourceTests
{
    private static readonly PermissionName Permission = new("Acme.Reports.Read");
    private static readonly Principal Alice = FakePrincipals.Account("issuer-a", "alice");

    private static readonly Regex ClaimsRead = new(@"\b[Pp]rincipal\w*\??\.Claims\b", RegexOptions.CultureInvariant);

    /// <summary>A provider whose grants live in a store it reads on every request.</summary>
    private sealed class StoreBackedProvider : IPermissionProvider
    {
        public HashSet<string> Store { get; } = [];

        public PermissionProviderName Name { get; } = new("Acme.StoreBacked");

        public Task<Result<IReadOnlySet<PermissionName>, AuthorizationError>> GrantsAsync(
            Principal principal, TenantId tenant, ResourceRef? resource, CancellationToken cancellationToken) =>
            Task.FromResult(Result<IReadOnlySet<PermissionName>, AuthorizationError>.Success(
                Store.Contains(principal.Id.ToString())
                    ? new HashSet<PermissionName> { Permission }
                    : new HashSet<PermissionName>()));
    }

    /// <summary>Reads its store once per principal and keeps the answer — the carried grant I-A10
    /// forbids.</summary>
    private sealed class CachingProvider(StoreBackedProvider inner) : IPermissionProvider
    {
        private readonly Dictionary<string, IReadOnlySet<PermissionName>> _seen = [];

        public PermissionProviderName Name { get; } = new("Acme.Caching");

        public async Task<Result<IReadOnlySet<PermissionName>, AuthorizationError>> GrantsAsync(
            Principal principal, TenantId tenant, ResourceRef? resource, CancellationToken cancellationToken)
        {
            var key = principal.Id.ToString();
            if (!_seen.TryGetValue(key, out var grants))
            {
                grants = (await inner.GrantsAsync(principal, tenant, resource, cancellationToken)).Value;
                _seen[key] = grants;
            }

            return Result<IReadOnlySet<PermissionName>, AuthorizationError>.Success(grants);
        }
    }

    /// <summary>Grants whatever the token's role claim says — the mistake #92 exists to prevent.</summary>
    private sealed class ClaimsReadingProvider : IPermissionProvider
    {
        public PermissionProviderName Name { get; } = new("Acme.ClaimsReading");

        public Task<Result<IReadOnlySet<PermissionName>, AuthorizationError>> GrantsAsync(
            Principal principal, TenantId tenant, ResourceRef? resource, CancellationToken cancellationToken) =>
            Task.FromResult(Result<IReadOnlySet<PermissionName>, AuthorizationError>.Success(
                principal.Claims is { } claims && claims.IsInRole("owner")
                    ? new HashSet<PermissionName> { Permission }
                    : new HashSet<PermissionName>()));
    }

    [Fact]
    public async Task A_provider_that_reads_its_source_on_every_request_passes_the_harness()
    {
        var provider = new StoreBackedProvider();

        await PermissionProviderHarness.AssertRevokedGrantDeniesNextRequestAsync(
            provider, Alice, TenantId.Implicit, null, Permission,
            grant: () => { provider.Store.Add(Alice.Id.ToString()); return Task.CompletedTask; },
            revoke: () => { provider.Store.Remove(Alice.Id.ToString()); return Task.CompletedTask; });
        await PermissionProviderHarness.AssertTokenClaimsGrantNothingAsync(provider, TenantId.Implicit, null, Permission);
    }

    [Fact]
    public async Task A_provider_that_carries_a_grant_between_requests_fails_the_revoke_check()
    {
        var store = new StoreBackedProvider();
        var provider = new CachingProvider(store);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PermissionProviderHarness.AssertRevokedGrantDeniesNextRequestAsync(
                provider, Alice, TenantId.Implicit, null, Permission,
                grant: () => { store.Store.Add(Alice.Id.ToString()); return Task.CompletedTask; },
                revoke: () => { store.Store.Remove(Alice.Id.ToString()); return Task.CompletedTask; }));

        Assert.Contains("still granted", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_provider_that_reads_a_grant_from_the_token_fails_the_claims_check()
    {
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PermissionProviderHarness.AssertTokenClaimsGrantNothingAsync(
                new ClaimsReadingProvider(), TenantId.Implicit, null, Permission));

        Assert.Contains("Claims are never a grant source", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_revoke_check_fails_a_provider_that_never_granted_instead_of_passing_vacuously()
    {
        var provider = new StoreBackedProvider();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PermissionProviderHarness.AssertRevokedGrantDeniesNextRequestAsync(
                provider, Alice, TenantId.Implicit, null, Permission,
                grant: () => Task.CompletedTask,
                revoke: () => Task.CompletedTask));

        Assert.Contains("did not grant", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>I-I6 — no Platform source file reads <c>Principal.Claims</c>. Identity writes a principal's
    /// claims without reading them back, and every other mention is a comment.</summary>
    [Fact]
    public void I_I6_no_Platform_source_reads_Principal_Claims()
    {
        var src = Path.Combine(RepositoryRoot(), "src");
        var separator = Path.DirectorySeparatorChar;

        var offenders = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{separator}obj{separator}", StringComparison.Ordinal)
                && !f.Contains($"{separator}bin{separator}", StringComparison.Ordinal))
            .SelectMany(f => File.ReadLines(f)
                .Select((line, i) => (File: f, Line: i + 1, Text: line))
                .Where(l => !l.Text.TrimStart().StartsWith("//", StringComparison.Ordinal) && ClaimsRead.IsMatch(l.Text)))
            .Select(l => $"{Path.GetRelativePath(src, l.File)}:{l.Line}: {l.Text.Trim()}")
            .ToList();

        Assert.Empty(offenders);
    }

    /// <summary>The scan must be able to fail: a line that reads the claims is matched, and a read of some
    /// other type's <c>Claims</c> is not.</summary>
    [Fact]
    public void I_I6_scan_matches_a_read_of_the_claims()
    {
        Assert.Matches(ClaimsRead, "var roles = principal.Claims.FindAll(\"role\");");
        Assert.Matches(ClaimsRead, "if (principal?.Claims is { } c) { }");
        Assert.DoesNotMatch(ClaimsRead, "var tier = effective?.Claims.Tier;");
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AGENTS.md")) && Directory.Exists(Path.Combine(directory.FullName, "src")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("The repository root was not found above the test output directory.");
    }
}
