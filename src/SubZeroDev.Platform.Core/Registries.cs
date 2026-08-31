using SubZeroDev.Platform.Abstractions;

namespace SubZeroDev.Platform.Core;

/// <summary>A module and its declared dependencies, in resolved order.</summary>
/// <param name="Name">The module's name.</param>
/// <param name="DependsOn">The modules it declared a dependency on.</param>
/// <param name="Module">The module itself.</param>
public sealed record ModuleDescriptor(
    ModuleName Name,
    IReadOnlyCollection<ModuleName> DependsOn,
    IPlatformModule Module);

/// <summary>Resolves the module graph into a reproducible order.</summary>
public interface IModuleRegistry
{
    /// <summary>Returns the topological order, ties broken by name, so the order is identical
    /// across runs on identical input regardless of discovery order.</summary>
    /// <param name="modules">The registered modules.</param>
    /// <returns>The resolved order, or the first graph defect found.</returns>
    Result<IReadOnlyList<ModuleDescriptor>, ModuleGraphError> Resolve(
        IReadOnlyCollection<IPlatformModule> modules);
}

/// <summary>Collects background-work registrations and scopes them by role.</summary>
public interface IBackgroundWorkRegistry
{
    /// <summary>Registers one unit of background work.</summary>
    /// <param name="work">The registration.</param>
    /// <returns>Success, or why the registration was rejected.</returns>
    Result<BackgroundWorkRegistrationError> Register(IBackgroundWork work);

    /// <summary>Everything registered, in registration order.</summary>
    IReadOnlyList<IBackgroundWork> Registered { get; }

    /// <summary>The registrations whose declared roles include this one. How Hosting starts work it
    /// cannot name.</summary>
    /// <param name="role">The host's role.</param>
    /// <returns>The registrations that run in that role.</returns>
    IReadOnlyList<IBackgroundWork> ForRole(HostRole role);

    /// <summary>Closes the registry. One-way: registration after this is a defect, not a condition.</summary>
    void Freeze();
}

/// <summary>Collects audit sink registrations.</summary>
public interface IAuditSinkRegistry
{
    /// <summary>Registers one sink.</summary>
    /// <param name="sink">The sink.</param>
    /// <returns>Success, or why the registration was rejected.</returns>
    Result<AuditSinkRegistrationError> Register(IAuditSink sink);

    /// <summary>Everything registered, in registration order.</summary>
    IReadOnlyList<IAuditSink> Registered { get; }

    /// <summary>Closes the registry. One-way: registration after this is a defect, not a condition.</summary>
    void Freeze();
}

/// <summary>Collects health check registrations.</summary>
public interface IHealthCheckRegistry
{
    /// <summary>Registers one check.</summary>
    /// <param name="check">The check.</param>
    /// <returns>Success, or why the registration was rejected.</returns>
    Result<HealthCheckRegistrationError> Register(IHealthCheck check);

    /// <summary>Everything registered, in registration order.</summary>
    IReadOnlyList<IHealthCheck> Registered { get; }

    /// <summary>Closes the registry. One-way.</summary>
    void Freeze();
}

/// <inheritdoc cref="IModuleRegistry"/>
internal sealed class ModuleRegistry : IModuleRegistry
{
    /// <inheritdoc/>
    public Result<IReadOnlyList<ModuleDescriptor>, ModuleGraphError> Resolve(
        IReadOnlyCollection<IPlatformModule> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);

        var byName = new Dictionary<ModuleName, IPlatformModule>();
        foreach (var module in modules)
        {
            if (!byName.TryAdd(module.Name, module))
            {
                return Result<IReadOnlyList<ModuleDescriptor>, ModuleGraphError>.Failure(
                    ModuleGraphError.DuplicateModuleName(module.Name));
            }
        }

        foreach (var module in byName.Values)
        {
            foreach (var dependency in module.DependsOn)
            {
                if (!byName.ContainsKey(dependency))
                {
                    return Result<IReadOnlyList<ModuleDescriptor>, ModuleGraphError>.Failure(
                        ModuleGraphError.MissingDependency(module.Name, dependency));
                }
            }
        }

        // Kahn's algorithm over a ready set kept in name order, which is what makes the result the
        // one topological order rather than any of them.
        var remaining = byName.Values.ToDictionary(
            module => module.Name,
            module => module.DependsOn.Distinct().Count());

        var dependents = byName.Values
            .SelectMany(module => module.DependsOn.Distinct().Select(dependency => (dependency, module.Name)))
            .GroupBy(edge => edge.dependency)
            .ToDictionary(group => group.Key, group => group.Select(edge => edge.Name).ToList());

        var ready = new SortedSet<ModuleName>(
            remaining.Where(entry => entry.Value == 0).Select(entry => entry.Key),
            ModuleNameComparer.Ordinal);

        var ordered = new List<ModuleDescriptor>(byName.Count);
        while (ready.Count > 0)
        {
            var next = ready.Min;
            ready.Remove(next);
            remaining.Remove(next);

            var module = byName[next];
            ordered.Add(new ModuleDescriptor(module.Name, module.DependsOn, module));

            if (!dependents.TryGetValue(next, out var waiting))
            {
                continue;
            }

            foreach (var dependent in waiting)
            {
                remaining[dependent]--;
                if (remaining[dependent] == 0)
                {
                    ready.Add(dependent);
                }
            }
        }

        return remaining.Count == 0
            ? Result<IReadOnlyList<ModuleDescriptor>, ModuleGraphError>.Success(ordered)
            : Result<IReadOnlyList<ModuleDescriptor>, ModuleGraphError>.Failure(
                ModuleGraphError.CyclicDependency(FindCycle(byName, remaining.Keys.ToHashSet())));
    }

    private static IReadOnlyList<ModuleName> FindCycle(
        IReadOnlyDictionary<ModuleName, IPlatformModule> byName,
        HashSet<ModuleName> candidates)
    {
        var start = candidates.OrderBy(name => name.Value, StringComparer.Ordinal).First();
        var path = new List<ModuleName>();
        var seen = new HashSet<ModuleName>();

        var current = start;
        while (seen.Add(current))
        {
            path.Add(current);
            var next = byName[current].DependsOn
                .Where(candidates.Contains)
                .OrderBy(name => name.Value, StringComparer.Ordinal)
                .FirstOrDefault();

            if (next == default)
            {
                break;
            }

            current = next;
        }

        path.Add(current);
        return path;
    }

    private sealed class ModuleNameComparer : IComparer<ModuleName>
    {
        internal static ModuleNameComparer Ordinal { get; } = new();

        public int Compare(ModuleName x, ModuleName y) =>
            string.CompareOrdinal(x.Value, y.Value);
    }
}

/// <inheritdoc cref="IBackgroundWorkRegistry"/>
internal sealed class BackgroundWorkRegistry : IBackgroundWorkRegistry
{
    private readonly List<IBackgroundWork> _registered = [];
    private readonly Lock _gate = new();
    private bool _frozen;

    /// <inheritdoc/>
    public IReadOnlyList<IBackgroundWork> Registered => _registered;

    /// <inheritdoc/>
    public Result<BackgroundWorkRegistrationError> Register(IBackgroundWork work)
    {
        ArgumentNullException.ThrowIfNull(work);

        lock (_gate)
        {
            if (_frozen)
            {
                return Result<BackgroundWorkRegistrationError>.Failure(
                    BackgroundWorkRegistrationError.RegistryFrozen(work.Name));
            }

            if (work.Roles == 0)
            {
                return Result<BackgroundWorkRegistrationError>.Failure(
                    BackgroundWorkRegistrationError.NoRoleDeclared(work.Name));
            }

            if (_registered.Any(registered => registered.Name == work.Name))
            {
                return Result<BackgroundWorkRegistrationError>.Failure(
                    BackgroundWorkRegistrationError.DuplicateName(work.Name));
            }

            _registered.Add(work);
            return Result<BackgroundWorkRegistrationError>.Success();
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<IBackgroundWork> ForRole(HostRole role)
    {
        var declared = role == HostRole.Web ? HostRoles.Web : HostRoles.Worker;
        return _registered.Where(work => work.Roles.HasFlag(declared)).ToList();
    }

    /// <inheritdoc/>
    public void Freeze()
    {
        lock (_gate)
        {
            _frozen = true;
        }
    }
}

/// <inheritdoc cref="IAuditSinkRegistry"/>
internal sealed class AuditSinkRegistry : IAuditSinkRegistry
{
    private readonly List<IAuditSink> _registered = [];
    private readonly Lock _gate = new();
    private bool _frozen;

    /// <inheritdoc/>
    public IReadOnlyList<IAuditSink> Registered => _registered;

    /// <inheritdoc/>
    public Result<AuditSinkRegistrationError> Register(IAuditSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);

        lock (_gate)
        {
            if (_frozen)
            {
                return Result<AuditSinkRegistrationError>.Failure(
                    AuditSinkRegistrationError.RegistryFrozen(sink.Name));
            }

            var existing = _registered.FirstOrDefault(
                registered => string.Equals(registered.Name, sink.Name, StringComparison.Ordinal));
            if (existing is not null)
            {
                return Result<AuditSinkRegistrationError>.Failure(
                    AuditSinkRegistrationError.DuplicateProviderName(
                        sink.Name, existing.GetType().Name, sink.GetType().Name));
            }

            _registered.Add(sink);
            return Result<AuditSinkRegistrationError>.Success();
        }
    }

    /// <inheritdoc/>
    public void Freeze()
    {
        lock (_gate)
        {
            _frozen = true;
        }
    }
}

/// <inheritdoc cref="IHealthCheckRegistry"/>
internal sealed class HealthCheckRegistry : IHealthCheckRegistry
{
    private readonly List<IHealthCheck> _registered = [];
    private readonly Lock _gate = new();
    private bool _frozen;

    /// <inheritdoc/>
    public IReadOnlyList<IHealthCheck> Registered => _registered;

    /// <inheritdoc/>
    public Result<HealthCheckRegistrationError> Register(IHealthCheck check)
    {
        ArgumentNullException.ThrowIfNull(check);

        lock (_gate)
        {
            if (_frozen)
            {
                return Result<HealthCheckRegistrationError>.Failure(
                    HealthCheckRegistrationError.RegistryFrozen(check.Name));
            }

            if (check is { Kind: HealthCheckKind.Liveness, TouchesExternalDependency: true })
            {
                return Result<HealthCheckRegistrationError>.Failure(
                    HealthCheckRegistrationError.ExternalDependencyInLivenessCheck(check.Name));
            }

            if (_registered.Any(registered => registered.Name == check.Name))
            {
                return Result<HealthCheckRegistrationError>.Failure(
                    HealthCheckRegistrationError.DuplicateName(check.Name));
            }

            _registered.Add(check);
            return Result<HealthCheckRegistrationError>.Success();
        }
    }

    /// <inheritdoc/>
    public void Freeze()
    {
        lock (_gate)
        {
            _frozen = true;
        }
    }
}
