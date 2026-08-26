using Ago.Calendar.Domain;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases;

/// <summary>
/// The expected failures a manual availability edit can end in, as <see cref="Error"/> values rather
/// than exceptions (coding-style.md: exceptions are for the unexpected). Codes are stable strings a
/// console can switch on and a future HTTP layer can map to a status; messages are for a human
/// operator, so they say what to do next rather than restating the code.
/// </summary>
public static class AvailabilityErrors
{
    public static Error DayNotMaterialized(DateOnly localDate) => new(
        "availability.day_not_materialized",
        $"{localDate:yyyy-MM-dd} has no generated slots yet, so there is nothing to edit. " +
        "A day can only be edited once it is inside the materialisation horizon.");

    public static Error DayHasBookings(DateOnly localDate) => new(
        "availability.day_has_bookings",
        $"{localDate:yyyy-MM-dd} already has a booking on it. Cancel the booking first - " +
        "the customer has to be told, and deleting the slot out from under them would not tell them.");

    public static Error DayChangedConcurrently(DateOnly localDate) => new(
        "availability.day_changed_concurrently",
        $"{localDate:yyyy-MM-dd} was booked while the edit was being applied. Reload the day and try again.");

    public static Error CalendarNotFound(CalendarId calendarId) => new(
        "availability.calendar_not_found", $"Calendar {calendarId.Value} does not exist.");

    public static Error WorkerNotOnCalendar(WorkerId workerId, CalendarId calendarId) => new(
        "availability.worker_not_on_calendar",
        $"Worker {workerId.Value} does not work in calendar {calendarId.Value}.");
}
