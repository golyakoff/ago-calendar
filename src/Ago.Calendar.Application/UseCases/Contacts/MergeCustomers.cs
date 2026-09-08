using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.UseCases.Contacts;

/// <summary>
/// `23-60`/`adr/0147`: "an operator can merge the two, deliberately." Carries the two candidate ids
/// unordered - neither is named "survivor" or "absorbed" here, because which one wins is
/// <see cref="MergeCustomersHandler"/>'s own decision, not the caller's. See that handler's own doc
/// comment for why: this product's only channel by which a *future* booking attaches to a customer is
/// <c>BookingStore</c>'s upsert on <c>(tenant_id, phone) WHERE source = 'Booking'</c>, which only a
/// <see cref="CustomerSource.Booking"/>-sourced row can ever be the target of - so letting an operator
/// tombstone that row in favour of a <see cref="CustomerSource.Chat"/>-sourced one would silently
/// misroute every booking this person makes from then on, the exact "invisible" failure mode
/// `adr/0147` warns a wrong merge already is, reintroduced by the tool built to fix duplicates.
/// </summary>
public readonly record struct MergeCustomers(
    OperatorId OperatorId, TenantId TenantId, CustomerId FirstCustomerId, CustomerId SecondCustomerId);

/// <summary>What the console's confirmation dialog and the audit trail both render back: which id
/// actually survived, which was tombstoned, and how many bookings moved - the same three facts
/// `ICustomerMergeStore`'s own remarks name as what a merge that can be explained afterwards
/// requires.</summary>
public readonly record struct CustomerMergeOutcome(
    CustomerId SurvivorCustomerId, CustomerId AbsorbedCustomerId, int BookingsMoved);
