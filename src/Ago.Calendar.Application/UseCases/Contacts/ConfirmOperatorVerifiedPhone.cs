using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.UseCases.Contacts;

/// <summary>`23-12`/`decisions.md` §5: "I called and it is them" - one customer, recorded once. See
/// <see cref="ConfirmOperatorVerifiedPhoneHandler"/> for why this is gated identically to a
/// reveal.</summary>
public readonly record struct ConfirmOperatorVerifiedPhone(OperatorId OperatorId, TenantId TenantId, CustomerId CustomerId);
