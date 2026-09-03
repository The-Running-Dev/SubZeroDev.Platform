using SubZeroDev.Platform.Abstractions;

namespace SubZeroDev.Platform.Persistence;

/// <summary>The tenant column. Not optional: every product table carries it from its first
/// migration, non-null, defaulting to the all-zero sentinel.</summary>
public interface ITenantOwned
{
    /// <summary>The row's tenant. <see cref="TenantId.Implicit"/> throughout D3.</summary>
    TenantId Tenant { get; }
}

/// <summary>The four audit columns. Times come from <see cref="IClock"/>; actors from the ambient
/// principal, which is total — every row names an actor.</summary>
public interface IAuditable
{
    /// <summary>When the row was created.</summary>
    DateTimeOffset CreatedAt { get; }

    /// <summary>Who created the row: the acting principal's <c>PrincipalId.ToString()</c>. Never
    /// split to recover the pair.</summary>
    string CreatedBy { get; }

    /// <summary>When the row was last modified.</summary>
    DateTimeOffset? ModifiedAt { get; }

    /// <summary>Who last modified the row. Null when there was no principal.</summary>
    string? ModifiedBy { get; }
}

/// <summary>The soft-delete columns. Opt-in per table: a soft delete nobody asked for silently
/// changes the meaning of every query against that table.</summary>
public interface ISoftDeletable
{
    /// <summary>Whether the row is soft-deleted.</summary>
    bool IsDeleted { get; }

    /// <summary>When the row was soft-deleted.</summary>
    DateTimeOffset? DeletedAt { get; }

    /// <summary>Who soft-deleted the row.</summary>
    string? DeletedBy { get; }
}

/// <summary>An entity type that may publish rows for reading by other tenants. Declared on the type
/// at model build; there is no per-row opt-in on an ordinary type. Not a Platform table — a
/// consumer's entity type declaring this acquires a <see cref="SharedAt"/> column in that consumer's
/// own migration, added nullable with no backfill (every existing row is private, which is the
/// correct starting state).</summary>
public interface IShareable : ITenantOwned
{
    /// <summary>When the owning tenant published the row, or null while it is private. The only
    /// representation of "private" — there is no separate boolean, because two columns that can
    /// disagree about the same fact will.</summary>
    DateTimeOffset? SharedAt { get; }
}
