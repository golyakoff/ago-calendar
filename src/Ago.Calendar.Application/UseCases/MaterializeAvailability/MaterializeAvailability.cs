using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.UseCases.MaterializeAvailability;

/// <summary>
/// Extend one calendar's availability to <paramref name="HorizonDays"/> business-local days past
/// today.
///
/// <para>Scoped to a single calendar rather than "do everything", because the calendar is the unit
/// that carries the two inputs the whole operation depends on - the IANA zone the rules are read in
/// and the buffer between slots - and a handler that looped over calendars internally would hold
/// one transaction open across tenants for no reason. Looping is the job's business
/// (<c>Ago.Calendar.Worker</c>), and it is why one slow or broken calendar cannot stop the
/// rest.</para>
/// </summary>
/// <param name="CalendarId">The calendar to extend.</param>
/// <param name="HorizonDays">How many days past today to cover. A configuration value with no
/// claimed-optimal default - see <c>AvailabilityMaterializationJobOptions.HorizonDays</c>.</param>
public readonly record struct MaterializeAvailability(CalendarId CalendarId, int HorizonDays);
