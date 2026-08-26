namespace Ago.Calendar.Contracts;

/// <summary>
/// The wire shapes of the public booking surface (`20-06`) - what a page embedding a tenant reads
/// before it can offer anybody a time.
///
/// <para><b>Every one of these is expressible as a prompt and a list of labelled choices</b>, which
/// is the constraint the 2026-08-26 boundary review put on this item and adr/0061 turned into a
/// mechanism: a message carries a kind, an opaque payload and actions, and a renderer on a channel
/// with no UI prints the actions as a numbered list. A response here is that payload's data, so a
/// booking flow driven by these DTOs renders as a grid in a browser and as
/// <c>1) 10:00  2) 10:45</c> over SMS. Nothing below is a coordinate, a pixel or a layout.</para>
///
/// <para><b>Ids are opaque to the renderer, and that is the load-bearing part.</b> An action's value
/// is a <see cref="Guid"/> the producer understands and the renderer never interprets - which is
/// exactly adr/0061's split, arrived at from the other end.</para>
/// </summary>
public sealed record BookingSurfaceResponse(string TenantName, IReadOnlyList<BookableCalendarResponse> Calendars);

public sealed record BookableCalendarResponse(
    Guid CalendarId, string Name, string TimeZone, IReadOnlyList<BookableServiceResponse> Services);

/// <param name="DurationMinutes">Whole minutes, so a renderer prints "45 min" without parsing a
/// duration format.</param>
public sealed record BookableServiceResponse(Guid ServiceId, string Name, int DurationMinutes);

public sealed record BookableWorkerResponse(Guid WorkerId, string DisplayName);

/// <param name="StartsAt">ISO-8601 with an explicit offset - always UTC on the wire (CLAUDE.md rule
/// 11). A renderer that knows the reader's zone formats it; one that does not renders UTC and labels
/// it.</param>
/// <param name="LocalDate">The shop's own business day (adr/0049). Carried beside the instant rather
/// than derived from it, so that grouping "Tuesday's times" never depends on the reader's own
/// zone - which is the whole reason the column is stored.</param>
public sealed record OpenSlotResponse(
    Guid BookingId,
    Guid WorkerId,
    string WorkerDisplayName,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    DateOnly LocalDate);
