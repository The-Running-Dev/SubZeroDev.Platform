namespace SubZeroDev.Platform.Abstractions;

/// <summary>The tenant a row belongs to. Opaque by design: collation and case sensitivity are not
/// correctness properties of a tenant comparison.</summary>
/// <param name="Value">The tenant's identifier.</param>
public readonly record struct TenantId(Guid Value)
{
    /// <summary>The well-known all-zero tenant, which is the only tenant in D3.</summary>
    public static TenantId Implicit { get; } = new(Guid.Empty);

    /// <summary>Parses a tenant identifier, returning <see langword="false"/> rather than throwing.</summary>
    /// <param name="candidate">The text to parse.</param>
    /// <param name="result">The parsed tenant, or <see langword="default"/> when parsing failed.</param>
    /// <returns><see langword="true"/> when <paramref name="candidate"/> was a tenant identifier.</returns>
    public static bool TryParse(string candidate, out TenantId result)
    {
        if (Guid.TryParse(candidate, out var value))
        {
            result = new TenantId(value);
            return true;
        }

        result = default;
        return false;
    }

    /// <inheritdoc/>
    public override string ToString() => Value.ToString("D");
}

/// <summary>The single value that stays greppable from an inbound request through any depth of
/// derived events. It is the originating trace-id, never a second identifier beside it.</summary>
/// <param name="TraceId">32 lowercase hexadecimal characters, never all-zero.</param>
public readonly record struct CorrelationId(string TraceId)
{
    /// <summary>The originating trace-id: 32 lowercase hexadecimal characters, never all-zero.</summary>
    public string TraceId { get; } = Normalise(TraceId);

    /// <summary>Parses a correlation identity, returning <see langword="false"/> rather than throwing.</summary>
    /// <param name="candidate">The text to parse.</param>
    /// <param name="result">The parsed correlation, or <see langword="default"/> when parsing failed.</param>
    /// <returns><see langword="true"/> when <paramref name="candidate"/> was a trace-id.</returns>
    public static bool TryParse(string candidate, out CorrelationId result)
    {
        if (IsTraceId(candidate))
        {
            result = new CorrelationId(candidate.ToLowerInvariant());
            return true;
        }

        result = default;
        return false;
    }

    /// <inheritdoc/>
    public override string ToString() => TraceId;

    private static string Normalise(string traceId)
    {
        ArgumentNullException.ThrowIfNull(traceId);

        var lowered = traceId.ToLowerInvariant();
        if (!IsTraceId(lowered))
        {
            throw new ArgumentException(
                "A correlation identity is 32 lowercase hexadecimal characters and is never all-zero.",
                nameof(traceId));
        }

        return lowered;
    }

    private static bool IsTraceId(string candidate)
    {
        if (candidate is not { Length: 32 })
        {
            return false;
        }

        var allZero = true;
        foreach (var character in candidate)
        {
            if (!Uri.IsHexDigit(character))
            {
                return false;
            }

            if (character != '0')
            {
                allZero = false;
            }
        }

        return !allZero;
    }
}

/// <summary>W3C trace context as it crosses a process or storage boundary. The traceparent is
/// complete, trace flags included, so the sampling decision travels with it.</summary>
/// <param name="TraceParent">A complete W3C <c>traceparent</c>, including trace flags.</param>
/// <param name="TraceState">The W3C <c>tracestate</c> when the origin carried one.</param>
public readonly record struct TraceContext(string TraceParent, string? TraceState)
{
    /// <summary>The trace-id carried by <see cref="TraceParent"/>.</summary>
    public string TraceId => TraceParent.Substring(3, 32);

    /// <summary>Whether the origin's sampling decision was to sample.</summary>
    public bool Sampled => (Convert.ToInt32(TraceParent.Substring(53, 2), 16) & 1) == 1;

    /// <summary>Parses W3C trace context, returning <see langword="false"/> rather than throwing.
    /// A malformed inbound header never fails a request.</summary>
    /// <param name="traceParent">The inbound <c>traceparent</c>.</param>
    /// <param name="traceState">The inbound <c>tracestate</c>, when present.</param>
    /// <param name="result">The parsed context, or <see langword="default"/> when parsing failed.</param>
    /// <returns><see langword="true"/> when <paramref name="traceParent"/> was well formed.</returns>
    public static bool TryParse(string traceParent, string? traceState, out TraceContext result)
    {
        result = default;

        if (traceParent is not { Length: 55 })
        {
            return false;
        }

        if (traceParent[2] != '-' || traceParent[35] != '-' || traceParent[52] != '-')
        {
            return false;
        }

        foreach (var (start, length) in new[] { (0, 2), (3, 32), (36, 16), (53, 2) })
        {
            for (var index = start; index < start + length; index++)
            {
                if (!Uri.IsHexDigit(traceParent[index]))
                {
                    return false;
                }
            }
        }

        if (!CorrelationId.TryParse(traceParent.Substring(3, 32), out _))
        {
            return false;
        }

        if (traceParent.Substring(36, 16) == "0000000000000000")
        {
            return false;
        }

        result = new TraceContext(traceParent.ToLowerInvariant(), traceState);
        return true;
    }
}

/// <summary>A running host's process instance identity. Two hosts of one role on one machine
/// differ, and a restart produces a new value.</summary>
/// <param name="Value">The instance identity.</param>
public readonly record struct InstanceId(string Value)
{
    /// <summary>The instance identity.</summary>
    public string Value { get; } = Names.Require(Value, nameof(Value));

    /// <inheritdoc/>
    public override string ToString() => Value;
}

/// <summary>A module's name, unique within the module graph.</summary>
/// <param name="Value">The module's name.</param>
public readonly record struct ModuleName(string Value)
{
    /// <summary>The module's name.</summary>
    public string Value { get; } = Names.Require(Value, nameof(Value));

    /// <inheritdoc/>
    public override string ToString() => Value;
}

/// <summary>A health check's name, unique within the health registry.</summary>
/// <param name="Value">The check's name.</param>
public readonly record struct HealthCheckName(string Value)
{
    /// <summary>The check's name.</summary>
    public string Value { get; } = Names.Require(Value, nameof(Value));

    /// <inheritdoc/>
    public override string ToString() => Value;
}

/// <summary>A background work registration's name, unique within the background-work registry.</summary>
/// <param name="Value">The registration's name.</param>
public readonly record struct BackgroundWorkName(string Value)
{
    /// <summary>The registration's name.</summary>
    public string Value { get; } = Names.Require(Value, nameof(Value));

    /// <inheritdoc/>
    public override string ToString() => Value;
}

/// <summary>The stable name a stored outbox row's <c>type</c> column carries. Supplied by an explicit
/// registration rather than read off the CLR type, because dispatch must get from a stored string to
/// a type and has no instance to ask.</summary>
/// <param name="Value">The event's stable name.</param>
public readonly record struct EventTypeName(string Value)
{
    /// <summary>The event's stable name.</summary>
    public string Value { get; } = Names.Require(Value, nameof(Value));

    /// <inheritdoc/>
    public override string ToString() => Value;
}

/// <summary>A BCP-47 language tag carried by the ambient scope and the outbox row's <c>culture</c>
/// column. Deliberately non-positional so its all-zero representation — no backing string — is
/// <see cref="Invariant"/>, which is what lets culture join the scope as an optional parameter
/// without every existing call site changing.</summary>
public readonly record struct CultureTag
{
    private readonly string? _value;

    /// <summary>Creates a culture tag. <see cref="string.Empty"/> normalises to <see cref="Invariant"/>'s
    /// representation, so there is exactly one way to mean "no preference expressed".</summary>
    /// <param name="value">A BCP-47 language tag, or <see cref="string.Empty"/> for the invariant.</param>
    public CultureTag(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _value = value.Length == 0 ? null : value;
    }

    /// <summary>The tag, never <see langword="null"/> — <see cref="string.Empty"/> for the invariant.</summary>
    public string Value => _value ?? string.Empty;

    /// <summary>The well-known invariant: the actor expressed no preference. Equal to
    /// <c>default(CultureTag)</c>, not merely equivalent to it by convention.</summary>
    public static CultureTag Invariant { get; }

    /// <summary>Parses a culture tag, returning <see langword="false"/> rather than throwing. Accepts
    /// exactly what <see cref="System.Globalization.CultureInfo.GetCultureInfo(string)"/> will later
    /// resolve, and the empty string as the invariant.</summary>
    /// <param name="candidate">The text to parse.</param>
    /// <param name="result">The parsed tag, or <see langword="default"/> when parsing failed.</param>
    /// <returns><see langword="true"/> when <paramref name="candidate"/> was a legal tag.</returns>
    public static bool TryParse(string candidate, out CultureTag result)
    {
        if (candidate is null)
        {
            result = default;
            return false;
        }

        if (candidate.Length == 0)
        {
            result = Invariant;
            return true;
        }

        try
        {
            System.Globalization.CultureInfo.GetCultureInfo(candidate);
        }
        catch (System.Globalization.CultureNotFoundException)
        {
            result = default;
            return false;
        }

        result = new CultureTag(candidate);
        return true;
    }

    /// <inheritdoc/>
    public override string ToString() => Value;
}

internal static class Names
{
    internal static string Require(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim();
    }
}
