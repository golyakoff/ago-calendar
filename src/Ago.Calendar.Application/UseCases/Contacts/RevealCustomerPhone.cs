using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.UseCases.Contacts;

/// <summary>`23-12`/`decisions.md` §5: "masked, revealed on demand, and the reveal is recorded" -
/// one customer, one reveal. <paramref name="Surface"/> is which screen asked (`23-12`'s own Scope,
/// in those words) - a plain string, not a closed enum, the same "nothing yet reads this back to
/// branch on it" reasoning `ago-chat`'s own <c>RevealVisitorContactDetail</c> gives for its identical
/// field.</summary>
public readonly record struct RevealCustomerPhone(
    OperatorId OperatorId, TenantId TenantId, CustomerId CustomerId, string Surface);
