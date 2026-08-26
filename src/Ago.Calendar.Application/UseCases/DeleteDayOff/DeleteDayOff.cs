using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.UseCases.DeleteDayOff;

/// <summary>
/// "This worker is closed on this day." Addressed by business-local day, not by an instant range,
/// because that is how the tenant thinks about it and because <see cref="Event.LocalDate"/> is
/// stored for exactly this query (adr/0049).
/// </summary>
/// <param name="OperatorId">Whose permission is checked. Added by `20-06`, which is the item that
/// gave this use case an HTTP surface: until then it had no caller outside a test, and a command with
/// no actor was honest about that. The moment an operator can reach it from a browser, "which
/// calendar" stops being the only question - <c>whose</c> calendar is the other one, and it is the one
/// every cross-tenant bug is made of.</param>
/// <param name="TenantId">Whose calendar. Never inferred from the operator row - see the handler,
/// and <c>PermissionChecker</c> for why filtering roles by the tenant the *action* names is what makes
/// a token claiming another tenant resolve to no roles at all.</param>
public readonly record struct DeleteDayOff(
    OperatorId OperatorId, TenantId TenantId, CalendarId CalendarId, WorkerId WorkerId, DateOnly LocalDate);
