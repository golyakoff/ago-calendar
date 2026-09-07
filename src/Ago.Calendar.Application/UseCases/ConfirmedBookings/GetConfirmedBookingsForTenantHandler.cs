using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.ConfirmedBookings;

/// <param name="From">Inclusive, business-local.</param>
/// <param name="To">Inclusive.</param>
public readonly record struct GetConfirmedBookingsForTenant(
    OperatorId OperatorId, TenantId TenantId, DateOnly From, DateOnly To);

/// <summary>
/// `23-34`: what is actually booked, across every calendar the tenant has - the screen the queue,
/// setup, workers, availability and contacts never answered (`docs/backlog/23-34-*.md`'s own Goal:
/// "the answer today is: open a customer card, or look at the widget as a visitor would").
///
/// <para><b>Gated on <see cref="Permission.CustomerRead"/> alone - the same permission
/// <c>GetTenantContactsHandler</c> checks, and for the identical reason its own doc comment gives:
/// both screens answer "may this operator see a customer's personal data", not two unrelated
/// questions.</b> This is a deliberate divergence from every other calendar console screen's own
/// frontend gate (`ago-console`'s <c>buildCalendarItems</c>, before this item: every one of the five
/// existing screens - queue included, despite its backend already accepting
/// <see cref="Permission.BookingReject"/> - is reachable in the nav only by a caller holding
/// <see cref="Permission.CalendarConfigure"/>, which the seeded "Operator" role never carries,
/// only "Admin"). The author's own scoping decision names the operator, not the administrator, as
/// this screen's floor ("покажем их как минимум роли оператора, чтобы он видел заполненность
/// мастера и дней") - <see cref="Permission.CustomerRead"/> is what the seeded Operator role already
/// holds (`Ago.Chat.Application.UseCases.RegisterSite.RegisterSiteHandler.OperatorRolePermissions`),
/// so gating here on it, rather than reusing the console-wide <see cref="Permission.CalendarConfigure"/>
/// convention, is what actually delivers that instruction instead of stating it and gating narrower
/// anyway. <c>docs/architecture/authorization.md</c> and this item's own report carry the rest of that
/// finding.</para>
///
/// <para><b>The actor check comes before the range check</b> - the same order
/// <c>GetWorkerSlotsHandler</c> uses and for the identical reason: a caller who may not read this
/// screen at all learns nothing about whether the range they guessed was well formed.</para>
/// </summary>
public sealed class GetConfirmedBookingsForTenantHandler(
    IConfirmedBookingReadStore bookings,
    IPermissionChecker permissions,
    IContactVisibilityProjectionStore visibility)
{
    public async Task<Result<IReadOnlyList<ConfirmedBookingRow>>> HandleAsync(
        GetConfirmedBookingsForTenant query, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            query.OperatorId, query.TenantId, Permission.CustomerRead, cancellationToken);
        if (!allowed)
        {
            return ConfirmedBookingsErrors.Forbidden(Permission.CustomerRead);
        }

        if (query.To < query.From)
        {
            return ConfirmedBookingsErrors.InvalidRange(query.From, query.To);
        }

        // `23-12`: the identical third, independent read `GetTenantContactsHandler`'s own remarks
        // give for itself - resolved only once the permission gate above has already passed, since
        // there is no reason to ask what rung a tenant is on for a caller about to be refused the
        // whole list anyway.
        var rung = await visibility.GetAsync(query.TenantId, cancellationToken);
        var mask = rung == ContactVisibility.MaskedWithReveal;

        var rows = await bookings.GetConfirmedForTenantAsync(
            query.TenantId, query.From, query.To, mask, cancellationToken);
        return Result<IReadOnlyList<ConfirmedBookingRow>>.Success(rows);
    }
}
