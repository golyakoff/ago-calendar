using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Abstractions;

/// <summary>
/// The public booking surface's read side (adr/0004): rows shaped for whatever is offering a customer
/// a choice, never aggregates.
///
/// <para><b>This is the port <c>IEventRepository</c> refused to grow.</b> That port's own remarks say
/// so in as many words - "there is also no availability query ... the first real caller is `20-06`'s
/// booking widget". This is that caller, and the query is here rather than there because listing four
/// hundred free slots must not materialise four hundred <see cref="Event"/> aggregates with their
/// invariants in order to print a time.</para>
///
/// <para><b>Every method is scoped to one calendar, and the calendar was resolved from a public key
/// whose tenant already passed the origin check.</b> Nothing here takes a tenant id from a caller, so
/// no method can be pointed at another tenant's rows by a parameter alone.</para>
///
/// <para><b>Nothing here returns personal data.</b> A worker's display name is the shop's own staff
/// list, published on its own booking page by definition; no customer name, phone or booking history
/// is reachable through this port, which is what makes it safe to serve unauthenticated.</para>
/// </summary>
public interface IBookingSurfaceReadStore
{
    /// <summary>The services a customer may pick on this calendar: those offered by at least one
    /// active worker who works in it. A service nobody on this calendar performs is not a choice, and
    /// offering it would produce a booking attempt that <c>BookEventHandler</c> rejects.</summary>
    Task<IReadOnlyList<BookableServiceRow>> ListServicesAsync(
        CalendarId calendarId, CancellationToken cancellationToken);

    /// <summary>The active workers on this calendar who perform one service.</summary>
    Task<IReadOnlyList<BookableWorkerRow>> ListWorkersAsync(
        CalendarId calendarId, ServiceId serviceId, CancellationToken cancellationToken);

    /// <summary>
    /// Free slots on one calendar, from <paramref name="notBefore"/> onward, long enough for the
    /// service and optionally narrowed to one worker.
    ///
    /// <para><b><c>status = 'Available'</c> here is a courtesy, not a decision</b> - the same
    /// division <c>BookEventHandler</c> draws. This read tells a customer what to try; the claim's own
    /// <c>WHERE</c> clause decides whether they got it. A slot listed here and taken a second later is
    /// an ordinary lost race (adr/0059), not a stale read to defend against.</para>
    /// </summary>
    Task<IReadOnlyList<OpenSlotRow>> ListOpenSlotsAsync(
        CalendarId calendarId,
        ServiceId serviceId,
        WorkerId? workerId,
        DateTimeOffset notBefore,
        int limit,
        CancellationToken cancellationToken);
}

/// <param name="DurationMinutes">Whole minutes - <see cref="Service"/> stores it that way for the
/// reason its own remarks give, and a wire contract that said "PT45M" would make every renderer parse
/// a duration to print "45 min".</param>
public readonly record struct BookableServiceRow(ServiceId ServiceId, string Name, int DurationMinutes);

public readonly record struct BookableWorkerRow(WorkerId WorkerId, string DisplayName);

/// <param name="LocalDate">The business-local day as the shop names it (adr/0049) - what a picker
/// groups by, so that grouping never depends on the reader's own zone.</param>
public readonly record struct OpenSlotRow(
    EventId EventId,
    WorkerId WorkerId,
    string WorkerDisplayName,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    DateOnly LocalDate);
