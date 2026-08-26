using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.UseCases.EditDayBoundary;

/// <summary>
/// "On this day this worker starts late / finishes early." One day, one worker, new opening and
/// closing times.
///
/// <para><b>The new boundaries are wall clock, and they have to be.</b> A tenant saying "on Tuesday
/// we open at eleven" is making a statement about a clock on a wall, exactly like the
/// <see cref="WorkingHoursRule"/> this overrides for one day - so the command carries
/// <see cref="TimeOnly"/> and the same single conversion turns it into instants. Taking a
/// <see cref="DateTimeOffset"/> instead would push the conversion out to the caller, which is a UI,
/// which is where a fixed offset gets picked and a shop opens an hour late in March.</para>
/// </summary>
public readonly record struct EditDayBoundary(
    CalendarId CalendarId, WorkerId WorkerId, DateOnly LocalDate, TimeOnly OpensAt, TimeOnly ClosesAt);
