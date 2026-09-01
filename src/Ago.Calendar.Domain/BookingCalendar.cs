namespace Ago.Calendar.Domain;

/// <summary>
/// A published booking surface - "the barbershop's calendar" - holding the workers a customer can
/// choose between and the time zone their working hours are read in.
///
/// <para><b>Why the type is not called <c>Calendar</c>.</b> It was, until the compiler said
/// otherwise: a type named <c>Calendar</c> declared in <c>Ago.Calendar.Domain</c> is unreferenceable
/// from any other project in this repository, because from inside <c>Ago.Calendar.Infrastructure.*</c>
/// the simple name <c>Calendar</c> resolves to the enclosing *namespace* <c>Ago.Calendar</c> before
/// a <c>using</c>-imported type is ever considered (CS0118, reproduced deliberately before choosing
/// this name). The alternatives were a <c>using</c> alias in every consuming file - which this
/// project's conventions rule out - or qualifying every reference as <c>Domain.Calendar</c>.
/// Renaming the CLR type is the cheapest of the three and costs nothing at the storage boundary: the
/// table is still <c>calendars</c>, the id is still <see cref="CalendarId"/>, the column is still
/// <c>calendar_id</c>.</para>
///
/// <para><b>The time zone is set once and never changed.</b> Every <see cref="Event"/> already
/// materialised carries a <see cref="Event.LocalDate"/> computed in this zone; moving the zone
/// afterwards would silently re-label days that are already booked, and the customer holding the
/// booking would not be told. Re-zoning a live calendar is a data migration with a human in the
/// loop, not a setter.</para>
/// </summary>
public sealed class BookingCalendar
{
    public CalendarId Id { get; }

    public TenantId TenantId { get; }

    public string Name { get; private set; } = string.Empty;

    /// <summary>The IANA zone this calendar's <see cref="WorkingHoursRule"/>s are read in - the one
    /// bridge between wall clock and instants. See <see cref="CalendarTimeZone"/>.</summary>
    public CalendarTimeZone TimeZone { get; }

    /// <summary>Whether the public booking surface shows this calendar - the same publish switch
    /// AGO Chat's per-site allowed origins express for its widget (`5-01`), reused as a concept, not
    /// as code.</summary>
    public bool IsPublished { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    private BookingCalendar(
        CalendarId id, TenantId tenantId, string name, CalendarTimeZone timeZone, DateTimeOffset now)
    {
        Id = id;
        TenantId = tenantId;
        Name = name;
        TimeZone = timeZone;
        CreatedAt = now;
    }

    // EF Core materialization only - never called by domain code.
    private BookingCalendar()
    {
    }

    public static BookingCalendar Create(
        CalendarId id, TenantId tenantId, string name, CalendarTimeZone timeZone, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return new BookingCalendar(id, tenantId, name.Trim(), timeZone, now);
    }

    public void Reconfigure(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Name = name.Trim();
    }

    public void Publish() => IsPublished = true;

    public void Unpublish() => IsPublished = false;
}
