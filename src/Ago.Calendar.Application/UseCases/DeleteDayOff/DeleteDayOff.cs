using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.UseCases.DeleteDayOff;

/// <summary>
/// "This worker is closed on this day." Addressed by business-local day, not by an instant range,
/// because that is how the tenant thinks about it and because <see cref="Event.LocalDate"/> is
/// stored for exactly this query (adr/0049).
/// </summary>
public readonly record struct DeleteDayOff(CalendarId CalendarId, WorkerId WorkerId, DateOnly LocalDate);
