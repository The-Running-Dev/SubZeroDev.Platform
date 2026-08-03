using SubZeroDev.Platform.Abstractions;

namespace SubZeroDev.Platform.Persistence;

/// <summary>The tenant column. Not optional: every product table carries it from its first
/// migration, non-null, defaulting to the all-zero sentinel.</summary>
public interface ITenantOwned
{
    /// <summary>The row's tenant. <see cref="TenantId.Implicit"/> throughout D3.</summary>
    TenantId Tenant { get; }
}

/// <summary>The four audit columns. Times come from <see cref="IClock"/>, actors from the ambient
/// principal, and both are null-tolerant because identity is D5 and there is frequently no
/// principal.</summary>
public interface IAuditable
{
    /// <summary>When the row was created.</summary>
    DateTimeOffset CreatedAt { get; }

    /// <summary>Who created the row. Null when there was no principal.</summary>
    string? CreatedBy { get; }

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
