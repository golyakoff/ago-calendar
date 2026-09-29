using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.UseCases.ErasePerson;

/// <summary>
/// `26-275`/`adr/0189`: <c>DELETE /api/v1/console/contacts/{personId}</c> - an Admin erases a client.
/// Calendar-initiated (not chat-initiated) because the one thing that can refuse it - whether this
/// person has a live future booking - is a rule-8 fact only this product's own database holds; a
/// chat-initiated erase would need a forbidden server-to-server read of the calendar's own live event
/// state (`ErasePersonHandler`'s own remarks). Carries nothing but the id and who is asking, the
/// identical "no new fact to validate, only the fact this row already is" shape
/// <see cref="Ago.Calendar.Application.UseCases.TenantErasure.EraseTenantData"/> already established
/// for its own whole-tenant sibling.
/// </summary>
/// <param name="OperatorId">Whose permission is checked (`customer:erase`, Admin-only -
/// `26-275` slice #1).</param>
/// <param name="TenantId">From the operator's own token, never from the request body -
/// <c>ConsoleEndpoints</c>'s own "the tenant comes from the principal" rule, restated here.</param>
/// <param name="PersonId">The opaque person id - the calendar's own reference (`adr/0184`), the same
/// id chat's Person row is keyed by. Named in the route, not the body: this deletes a resource the
/// route already identifies, the identical shape <c>DeleteWorker</c> and every other
/// <c>DELETE /console/...</c> route already takes.</param>
public readonly record struct ErasePerson(OperatorId OperatorId, TenantId TenantId, Guid PersonId);
