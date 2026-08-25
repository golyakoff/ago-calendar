namespace Ago.Calendar.Domain;

/// <summary>
/// Why a booking ended up <see cref="EventStatus.Cancelled"/>. Both paths reach the same status, and
/// they are still not the same event: a veto inside the confirmation window is something the
/// customer was told to expect, while cancelling a confirmed visit is a broken promise. `20-05`'s
/// SMS wording differs between them, which is the concrete reason this is carried on the domain
/// event rather than inferred later from a timestamp comparison.
/// </summary>
public enum CancellationReason
{
    /// <summary>An operator vetoed the claim before it was confirmed.</summary>
    RejectedByOperator,

    /// <summary>An already-confirmed booking was cancelled.</summary>
    CancelledByOperator,
}
