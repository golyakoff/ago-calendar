using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Abstractions;

/// <summary>
/// `23-60`/`adr/0147`: the write side of "an operator can merge the two, deliberately" - one
/// transaction that reassigns every booking the losing customer ever had, persists both aggregates'
/// own already-decided new state, and writes the audit row `adr/0147`'s own "a merge that cannot be
/// explained afterwards is a merge nobody will trust" calls for. All three, or none: a crash between
/// the booking reassignment and the audit write would otherwise leave bookings moved with nothing to
/// say who moved them, or an audit row for a merge whose bookings never actually followed - either
/// one is exactly the "invisible, irreversible" failure mode `adr/0147` warns a wrong merge already
/// is, moved here rather than avoided.
///
/// <para><b>Takes two already-mutated aggregates, not two ids and a rule this port re-derives.</b>
/// <c>MergeCustomersHandler</c> calls <see cref="Customer.AbsorbHistoryFrom"/> and
/// <see cref="Customer.MarkMergedInto"/> first - the same "the domain method is the precondition's
/// canonical statement" split <c>ICustomerRepository</c>'s own remarks describe for
/// <see cref="Customer.RecordVerifiedPhone"/>: what changes and why is decided in
/// <c>Ago.Calendar.Domain</c>, where the invariant lives and can be unit-tested with no database;
/// this port only has to persist the decision atomically alongside a table (<c>events</c>) the
/// aggregate itself never touches.</para>
///
/// <para><b><paramref name="tenantId"/> is not redundant with <c>survivor.TenantId</c>.</b> The bulk
/// reassignment of <c>events.customer_id</c> below is scoped to it in its own <c>WHERE</c> clause, on
/// top of matching the absorbed customer's id - so a caller cannot merge across tenants by construction
/// even if <c>MergeCustomersHandler</c>'s own tenant check were ever bypassed by a future bug. This is
/// the "structurally impossible, not just checked" shape the item's own "never" scope line asks for:
/// the query itself cannot express a cross-tenant reassignment, rather than refusing one at runtime.</para>
/// </summary>
public interface ICustomerMergeStore
{
    Task<CustomerMergeResult> MergeAsync(
        TenantId tenantId,
        Customer survivor,
        Customer absorbed,
        OperatorId operatorId,
        Guid mergeId,
        DateTimeOffset mergedAt,
        CancellationToken cancellationToken);
}

/// <summary>What the confirmation screen and the audit trail both want back: how many bookings
/// actually moved, so "0 bookings moved" (two contacts that happened to share a phone but neither
/// ever booked) reads differently from a merge that reattributed a real history.</summary>
public sealed record CustomerMergeResult(int BookingsMoved);
