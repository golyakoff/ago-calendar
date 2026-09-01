using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.UseCases.MaterializeAvailability;

/// <summary>
/// Extend one calendar's availability - every active worker on it, out to whatever each worker's own
/// <see cref="WorkerSchedule.HorizonDays"/> and <see cref="WorkerSchedule.MaterializeFrom"/> cursor
/// say.
///
/// <para><b>`20-14` removed the horizon parameter this command used to carry.</b> It was one number
/// for the whole calendar; the item's own "Decided" section moved the horizon to the worker's own
/// schedule, so a calendar with a fast-growing barber and a part-time colourist can keep two different
/// windows generated. Scoped to a single calendar for the same reason the pre-`20-14` version was -
/// the calendar is still the unit that carries the IANA zone every wall-clock conversion resolves
/// against, and looping over calendars stays the job's business, not this handler's
/// (<c>Ago.Calendar.Worker</c>).</para>
/// </summary>
/// <param name="CalendarId">The calendar to extend.</param>
public readonly record struct MaterializeAvailability(CalendarId CalendarId);
