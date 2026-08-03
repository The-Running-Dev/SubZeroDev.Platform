namespace SubZeroDev.Platform.Abstractions;

/// <summary>Marks a setting whose value two hosts disagreeing on changes what happens to rows they
/// share — one that decides outcomes, not merely timing. The marker is what makes that membership
/// checkable rather than a list maintained in prose beside a hash function.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class FingerprintedAttribute : Attribute;
