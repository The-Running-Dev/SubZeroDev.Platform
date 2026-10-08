using System.Globalization;
using SubZeroDev.Platform.Abstractions;

namespace SubZeroDev.Platform.RuntimeSettings;

/// <summary>A runtime setting's stable name. Compared ordinally and never parsed; the
/// <c>Platform.</c> prefix is reserved for Platform's own settings.</summary>
/// <param name="Value">The name.</param>
public readonly record struct SettingName(string Value)
{
    /// <summary>The name, never empty.</summary>
    public string Value { get; } = string.IsNullOrWhiteSpace(Value)
        ? throw new ArgumentException("A setting name is not empty.", nameof(Value))
        : Value;

    /// <inheritdoc/>
    public override string ToString() => Value;
}

/// <summary>Where a runtime setting's value can be stored. Resolution runs user, then tenant, then
/// global, then the declared default (I-ST1).</summary>
public enum SettingLayer
{
    /// <summary>The installation's value, stored under the implicit tenant.</summary>
    Global,

    /// <summary>The current tenant's value. Absent under the implicit tenant.</summary>
    Tenant,

    /// <summary>The current account's value in the current tenant. Absent for any principal that is
    /// not an <see cref="PrincipalKind.Account"/>, and under the implicit tenant.</summary>
    User,
}

/// <summary>One declared runtime setting: its name, the layers it may be set at, its default and its
/// rule. Only the three factories construct one, so a setting is a boolean, a 64-bit integer or text
/// and nothing else (<c>design/20-contract.md</c>, Types §15).</summary>
public abstract class SettingDefinition
{
    private protected SettingDefinition(SettingName name, IReadOnlySet<SettingLayer> layers)
    {
        ArgumentNullException.ThrowIfNull(layers);
        foreach (var layer in layers)
        {
            if (!Enum.IsDefined(layer))
            {
                throw new ArgumentOutOfRangeException(nameof(layers), "A layer is Global, Tenant or User.");
            }
        }

        Name = name;
        Layers = new HashSet<SettingLayer>(layers);
    }

    /// <summary>The setting's name.</summary>
    public SettingName Name { get; }

    /// <summary>The layers the setting may be set at. An empty set fails startup.</summary>
    public IReadOnlySet<SettingLayer> Layers { get; }

    /// <summary>Whether the setting's own default satisfies its rule. Checked once, when the catalogue
    /// is built.</summary>
    internal abstract bool DefaultIsValid { get; }

    /// <summary>Declares a boolean setting.</summary>
    /// <param name="name">The name.</param>
    /// <param name="defaultValue">The value read when no admitted layer holds one.</param>
    /// <param name="layers">The layers the setting may be set at.</param>
    /// <returns>The declaration.</returns>
    public static SettingDefinition<bool> Boolean(
        SettingName name, bool defaultValue, IReadOnlySet<SettingLayer> layers) =>
        new(name, defaultValue, layers, static _ => true);

    /// <summary>Declares a 64-bit integer setting.</summary>
    /// <param name="name">The name.</param>
    /// <param name="defaultValue">The value read when no admitted layer holds one.</param>
    /// <param name="layers">The layers the setting may be set at.</param>
    /// <param name="isValid">The rule every value, the default included, must satisfy.</param>
    /// <returns>The declaration.</returns>
    public static SettingDefinition<long> Integer(
        SettingName name, long defaultValue, IReadOnlySet<SettingLayer> layers, Func<long, bool>? isValid = null) =>
        new(name, defaultValue, layers, isValid ?? (static _ => true));

    /// <summary>Declares a text setting.</summary>
    /// <param name="name">The name.</param>
    /// <param name="defaultValue">The value read when no admitted layer holds one.</param>
    /// <param name="layers">The layers the setting may be set at.</param>
    /// <param name="isValid">The rule every value, the default included, must satisfy.</param>
    /// <returns>The declaration.</returns>
    public static SettingDefinition<string> Text(
        SettingName name, string defaultValue, IReadOnlySet<SettingLayer> layers, Func<string, bool>? isValid = null)
    {
        ArgumentNullException.ThrowIfNull(defaultValue);
        return new(name, defaultValue, layers, isValid ?? (static _ => true));
    }
}

/// <summary>A runtime setting of one of the three value types.</summary>
/// <typeparam name="T"><see cref="bool"/>, <see cref="long"/> or <see cref="string"/>.</typeparam>
public sealed class SettingDefinition<T> : SettingDefinition
    where T : notnull
{
    private readonly Func<T, bool> rule;

    internal SettingDefinition(SettingName name, T defaultValue, IReadOnlySet<SettingLayer> layers, Func<T, bool> rule)
        : base(name, layers)
    {
        Default = defaultValue;
        this.rule = rule;
    }

    /// <summary>The value read when no admitted layer holds one.</summary>
    public T Default { get; }

    /// <inheritdoc/>
    internal override bool DefaultIsValid => IsValid(Default);

    /// <summary>Whether a value satisfies the setting's rule.</summary>
    /// <param name="value">The value.</param>
    /// <returns><see langword="true"/> when the rule admits it.</returns>
    public bool IsValid(T value) => value is not null && rule(value);

    /// <summary>Parses a stored value: <c>true</c>/<c>false</c>, an invariant-culture integer, or the
    /// text as given (<c>design/20-contract.md</c>, Schemas §9). Validity under the rule is checked
    /// separately.</summary>
    internal bool TryParse(string stored, out T value)
    {
        object? parsed = Default switch
        {
            bool => stored switch
            {
                "true" => true,
                "false" => false,
                _ => null,
            },
            long => long.TryParse(stored, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number)
                ? number
                : null,
            string => stored,
            _ => null,
        };

        value = parsed is T typed ? typed : default!;
        return parsed is T;
    }
}

/// <summary>One product's declared runtime settings. A host registers each with
/// <c>services.AddSingleton&lt;ISettingCatalog, TCatalog&gt;()</c>; every catalogue is read once,
/// before the host serves, and the union is frozen (I-ST2).</summary>
public interface ISettingCatalog
{
    /// <summary>The settings this catalogue declares.</summary>
    IReadOnlyCollection<SettingDefinition> Declares { get; }
}

/// <summary>A read's answer: the value and the layer that held it.</summary>
/// <typeparam name="T">The setting's value type.</typeparam>
/// <param name="Value">The value.</param>
/// <param name="From">The layer that answered, or <see langword="null"/> for the declared
/// default.</param>
public sealed record ResolvedSetting<T>(T Value, SettingLayer? From)
    where T : notnull;

/// <summary>The two permissions a global or tenant write is evaluated against (I-ST3).</summary>
public static class RuntimeSettingsPermissions
{
    /// <summary>Writing a setting's global layer.</summary>
    public static PermissionName WriteGlobal { get; } = new("Platform.RuntimeSettings.WriteGlobal");

    /// <summary>Writing a setting's tenant layer.</summary>
    public static PermissionName WriteTenant { get; } = new("Platform.RuntimeSettings.WriteTenant");
}

/// <summary>The audit actions a change to a setting records (I-ST4).</summary>
public static class RuntimeSettingsAuditActions
{
    /// <summary>A layer's value was set.</summary>
    public static AuditAction SettingSet { get; } = new("platform.runtime-settings.set");

    /// <summary>A layer's stored value was removed.</summary>
    public static AuditAction SettingCleared { get; } = new("platform.runtime-settings.cleared");
}
