using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.UseCases.Contacts;

/// <summary>`23-60`/`adr/0147`: "seeing both sets of bookings before deciding" - what the console
/// fetches to render the confirmation step, before it ever calls <see cref="MergeCustomers"/>. Two
/// unordered candidate ids, the identical shape that command itself takes and for the identical
/// reason: nothing about a preview should presuppose which side would survive, since that is decided
/// only if the operator goes on to confirm.</summary>
public readonly record struct GetCustomerMergePreview(
    OperatorId OperatorId, TenantId TenantId, CustomerId FirstCustomerId, CustomerId SecondCustomerId);
