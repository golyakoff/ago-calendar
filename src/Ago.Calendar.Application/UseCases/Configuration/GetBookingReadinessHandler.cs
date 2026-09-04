using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.Configuration;

public readonly record struct GetBookingReadiness(OperatorId OperatorId, TenantId TenantId);

/// <summary>
/// `23-23`: the six things `flows.md` 3.1's own scope names, in the order it names them. A named
/// enum rather than a string, so a caller (and a test) can switch on exactly which one failed instead
/// of pattern-matching a sentence - the item's own words: "a list of named, ordered preconditions
/// with a met/unmet state, not a single boolean... a prose string satisfies neither."
/// </summary>
public enum BookingPrecondition
{
    CalendarPublished,
    WorkerOnCalendar,
    ServiceOffered,
    WorkingHoursConfigured,
    ScheduleSaved,
    SlotsMaterialized,
}

public readonly record struct PreconditionState(BookingPrecondition Precondition, bool IsMet);

/// <param name="CalendarId">Null only on <see cref="GetBookingReadinessHandler"/>'s own placeholder
/// for a tenant with no calendar at all - see its remarks.</param>
/// <param name="IsBookable">Every precondition met. Computed here, once, so the console never has to
/// fold six booleans itself and risk folding them wrong.</param>
public readonly record struct CalendarReadiness(
    CalendarId? CalendarId,
    string? CalendarName,
    bool IsBookable,
    IReadOnlyList<PreconditionState> Preconditions);

/// <summary>
/// `23-23`: not "is this tenant ready" as a boolean, but which of the six things `flows.md` 3.1's own
/// scope names is not yet true - see <see cref="IBookingReadinessReadStore"/> for where each fact
/// comes from and why it is computed as a funnel over one calendar's own workers rather than as six
/// independent existence checks.
///
/// <para><b>One entry per calendar the tenant has created, in the read store's own creation order.</b>
/// This handler adds no ordering of its own - <see cref="IBookingReadinessReadStore"/> already orders
/// by <c>created_at</c>, and re-sorting here would be a second place that order could disagree with
/// the SQL that actually produced it.</para>
///
/// <para><b>A tenant with no calendar gets one synthetic entry, <see cref="NothingConfigured"/>,
/// instead of an empty list.</b> Done-when's first clause asks for "every precondition unmet, in a
/// stable order" for a tenant with nothing set up - an empty list would report nothing rather than
/// report six unmet facts, and the console would have to invent this placeholder itself, once per
/// screen, to show anything at all. Assembling it here rather than in the read store is the same
/// layering call `GetTenantConfigurationHandler` already makes for its own shape: interpreting "zero
/// rows" as "here is what a first-run tenant sees" is a decision about what the fact means to a
/// screen, not a fact <c>calendars</c>/<c>workers</c>/... themselves state - Application's job, not
/// Infrastructure's.</para>
///
/// <para><b>Gated on <see cref="Permission.CalendarConfigure"/></b>, same as every other setup read -
/// <see cref="GetTenantConfigurationHandler"/>'s own remark on why applies unchanged: this is a read a
/// tenant makes while still configuring itself, not the public booking surface.</para>
/// </summary>
public sealed class GetBookingReadinessHandler(
    IBookingReadinessReadStore readiness, IPermissionChecker permissions, IClock clock)
{
    /// <summary>`flows.md` 3.1's own stated order - calendar, worker, service, hours, schedule,
    /// slots - the one order every caller sees, whether the tenant has a calendar or not.</summary>
    private static readonly IReadOnlyList<BookingPrecondition> Order =
    [
        BookingPrecondition.CalendarPublished,
        BookingPrecondition.WorkerOnCalendar,
        BookingPrecondition.ServiceOffered,
        BookingPrecondition.WorkingHoursConfigured,
        BookingPrecondition.ScheduleSaved,
        BookingPrecondition.SlotsMaterialized,
    ];

    private static readonly CalendarReadiness NothingConfigured = new(
        CalendarId: null,
        CalendarName: null,
        IsBookable: false,
        Preconditions: [.. Order.Select(precondition => new PreconditionState(precondition, IsMet: false))]);

    public async Task<Result<IReadOnlyList<CalendarReadiness>>> HandleAsync(
        GetBookingReadiness query, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            query.OperatorId, query.TenantId, Permission.CalendarConfigure, cancellationToken);
        if (!allowed)
        {
            return ConfigurationErrors.Forbidden(Permission.CalendarConfigure);
        }

        var rows = await readiness.GetForTenantAsync(query.TenantId, clock.UtcNow, cancellationToken);
        if (rows.Count == 0)
        {
            return Result<IReadOnlyList<CalendarReadiness>>.Success([NothingConfigured]);
        }

        return Result<IReadOnlyList<CalendarReadiness>>.Success([.. rows.Select(ToReadiness)]);
    }

    private static CalendarReadiness ToReadiness(CalendarReadinessRow row)
    {
        var met = new Dictionary<BookingPrecondition, bool>
        {
            [BookingPrecondition.CalendarPublished] = row.IsPublished,
            [BookingPrecondition.WorkerOnCalendar] = row.HasWorker,
            [BookingPrecondition.ServiceOffered] = row.HasWorkerWithService,
            [BookingPrecondition.WorkingHoursConfigured] = row.HasWorkingHours,
            [BookingPrecondition.ScheduleSaved] = row.HasSchedule,
            [BookingPrecondition.SlotsMaterialized] = row.HasFutureSlots,
        };

        var preconditions = Order
            .Select(precondition => new PreconditionState(precondition, met[precondition]))
            .ToList();

        return new CalendarReadiness(
            row.CalendarId, row.CalendarName, preconditions.All(state => state.IsMet), preconditions);
    }
}
