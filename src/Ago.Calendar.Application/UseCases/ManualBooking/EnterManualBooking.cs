using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.UseCases.ManualBooking;

/// <summary>
/// `26-268`/`adr/0188`: an operator re-enters a booking that was taken by phone, before this product
/// existed for the tenant - «Добавить вручную». Blocks the slot through the identical atomic claim
/// every other booking on this calendar goes through, straight into <see cref="EventStatus.Booked"/>
/// with no veto window: the operator is the confirmation authority for a visit they already agreed to
/// over the phone.
/// </summary>
/// <param name="OperatorId">Whose permission is checked (`booking:create`).</param>
/// <param name="TenantId">From the operator's own token, never from the request body - the identical
/// "the tenant comes from the principal, not a claim the caller typed" rule every console command in
/// this product follows (<c>ConsoleEndpoints</c>'s own remarks).</param>
/// <param name="CalendarId">Which calendar the slot is on - part of the claim's own <c>WHERE</c>
/// clause, not a pre-check (an id on another calendar is unclaimable by construction).</param>
/// <param name="ServiceId">What the client is booking - resolved to size the run
/// (<c>ConsecutiveRunFinder</c>) and to reject a worker who does not offer it.</param>
/// <param name="WorkerId">Who the client is seeing - named directly by the operator, unlike the
/// visitor path, which derives it from the slot the customer already clicked.</param>
/// <param name="StartEventId">The <see cref="EventStatus.Available"/> grid slot the operator picked as
/// the run's own start, by its own id - the same "name the slot the caller actually chose, never a
/// wall-clock instant" convention <c>BookEvent</c> and <c>RescheduleBooking</c> both use.</param>
/// <param name="DisplayName">What the operator typed as the client's name over the phone. Never
/// blank-vs-null ambiguous by the time it reaches the store - the handler trims and normalises blank to
/// <see langword="null"/>, the identical rule <c>BookEventHandler</c> applies to its own
/// <c>PersonRegistration.DisplayName</c>.</param>
/// <param name="Phone">The client's phone, raw as the operator typed it - validated by
/// <see cref="PhoneNumber"/>'s own constructor before anything else runs.</param>
public readonly record struct EnterManualBooking(
    OperatorId OperatorId,
    TenantId TenantId,
    CalendarId CalendarId,
    ServiceId ServiceId,
    WorkerId WorkerId,
    EventId StartEventId,
    string DisplayName,
    string Phone);
