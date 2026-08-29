using Ago.Calendar.Domain;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Ago.Calendar.Infrastructure.Postgres.Persistence;

/// <summary>
/// One converter per strongly-typed id - explicit and compiled, rather than one reflection-based
/// generic converter that would run per row materialized (the same call ago-chat made).
/// </summary>
internal static class IdConverters
{
    public static readonly ValueConverter<TenantId, Guid> Tenant = new(id => id.Value, value => new TenantId(value));
    public static readonly ValueConverter<OperatorId, Guid> Operator = new(id => id.Value, value => new OperatorId(value));
    public static readonly ValueConverter<RoleId, Guid> Role = new(id => id.Value, value => new RoleId(value));
    public static readonly ValueConverter<WorkerId, Guid> Worker = new(id => id.Value, value => new WorkerId(value));
    public static readonly ValueConverter<ServiceId, Guid> Service = new(id => id.Value, value => new ServiceId(value));
    public static readonly ValueConverter<CalendarId, Guid> Calendar = new(id => id.Value, value => new CalendarId(value));
    public static readonly ValueConverter<CustomerId, Guid> Customer = new(id => id.Value, value => new CustomerId(value));
    public static readonly ValueConverter<WorkingHoursRuleId, Guid> WorkingHoursRule = new(id => id.Value, value => new WorkingHoursRuleId(value));
    public static readonly ValueConverter<EventId, Guid> Event = new(id => id.Value, value => new EventId(value));
    public static readonly ValueConverter<ChatBookingTaskId, Guid> ChatBookingTask = new(
        id => id.Value, value => new ChatBookingTaskId(value));

    public static readonly ValueConverter<ServiceId?, Guid?> NullableService = new(
        id => id.HasValue ? id.Value.Value : (Guid?)null,
        value => value.HasValue ? new ServiceId(value.Value) : (ServiceId?)null);

    public static readonly ValueConverter<CustomerId?, Guid?> NullableCustomer = new(
        id => id.HasValue ? id.Value.Value : (Guid?)null,
        value => value.HasValue ? new CustomerId(value.Value) : (CustomerId?)null);

    /// <summary>`20-07`: <see cref="ChatBookingTask.WorkerId"/> is unset until the visitor picks one.</summary>
    public static readonly ValueConverter<WorkerId?, Guid?> NullableWorker = new(
        id => id.HasValue ? id.Value.Value : (Guid?)null,
        value => value.HasValue ? new WorkerId(value.Value) : (WorkerId?)null);

    /// <summary>`20-07`: <see cref="ChatBookingTask.EventId"/> is unset until a slot is chosen, and
    /// cleared again by <see cref="Ago.Calendar.Domain.ChatBookingTask.ReopenForSlotChoice"/>.</summary>
    public static readonly ValueConverter<EventId?, Guid?> NullableEvent = new(
        id => id.HasValue ? id.Value.Value : (Guid?)null,
        value => value.HasValue ? new EventId(value.Value) : (EventId?)null);

    /// <summary>
    /// The phone number's own converter. Note the asymmetry: writing goes through the already
    /// normalised <see cref="PhoneNumber.Value"/>, and reading goes back through the constructor,
    /// which normalises again. That is deliberate - a row written before a normalisation rule
    /// changed still materialises into a valid value object, and a row that somehow holds garbage
    /// fails loudly at materialisation rather than flowing into a comparison that quietly never
    /// matches.
    /// </summary>
    public static readonly ValueConverter<PhoneNumber, string> Phone = new(
        phone => phone.Value, value => new PhoneNumber(value));

    public static readonly ValueConverter<CalendarTimeZone, string> TimeZone = new(
        zone => zone.Value, value => new CalendarTimeZone(value));

    /// <summary>
    /// A service duration is stored as whole minutes in an <c>int</c>, not as a Postgres
    /// <c>interval</c>. Two reasons, both practical: an integer column is trivially comparable and
    /// orderable in SQL for the availability queries `20-02` will write, and
    /// <see cref="Service"/>'s own invariant is already "a whole number of minutes", so the column
    /// says exactly what the domain guarantees rather than a wider type the domain then narrows.
    /// </summary>
    public static readonly ValueConverter<TimeSpan, int> DurationMinutes = new(
        duration => (int)duration.TotalMinutes, minutes => TimeSpan.FromMinutes(minutes));

    /// <summary>
    /// <see cref="Permission"/> is a <c>readonly record struct</c> over a string, so the column is
    /// just the string - the wrapper exists to stop a permission being passed where a role name is
    /// expected, which is a compile-time concern with no storage consequence.
    /// </summary>
    /// (Named <c>PermissionName</c> rather than <c>Permission</c> because a static field sharing a
    /// name with the type it constructs makes <c>new Permission(value)</c> ambiguous to the compiler.)
    public static readonly ValueConverter<Permission, string> PermissionName = new(
        permission => permission.Value, value => new Permission(value));
}
