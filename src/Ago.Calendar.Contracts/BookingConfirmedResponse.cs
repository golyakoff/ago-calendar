namespace Ago.Calendar.Contracts;

/// <summary>
/// What a customer is told when a booking succeeds.
///
/// <para><b>Every field this type does not have is the point of it.</b> The row this response
/// describes is <c>PendingConfirmation</c> with a deadline, and an operator may still veto it
/// (`20-04`). The customer is told none of that, and the type is shaped so that no endpoint can tell
/// them by accident: there is no <c>status</c> field to set to <c>"pending"</c>, no
/// <c>confirmationDeadline</c> to render as a countdown, and no <c>state</c> the widget could switch
/// on. Adding one is not a small change - it is a reversal of the product spec's central design
/// decision, which is that the uncertainty of a booking sits with the business, who is watching a
/// queue, and not with the customer, who is watching a page.</para>
///
/// <para><c>BookingConfirmationDisclosureTests</c> holds that as an assertion rather than as this
/// paragraph: it serialises a real response and fails if the JSON mentions a pending state or a
/// deadline at all.</para>
///
/// <para>An id is returned so a customer can be given a reference and `20-06`'s console can link to
/// it. It is the event's own id: a booking and the slot it took are one row (adr/0049), so there is
/// no second identifier to invent.</para>
/// </summary>
/// <param name="BookingId">The booked slot. Quote it to the shop.</param>
/// <param name="WorkerId">Who the customer is seeing.</param>
/// <param name="StartsAt">ISO-8601 with an explicit offset (date-and-time.md). UTC on the wire; a
/// client renders it in whatever zone the customer is in, which is a rendering parameter this
/// product deliberately stores nowhere (adr/0049).</param>
/// <param name="EndsAt">Exclusive.</param>
/// <param name="LocalDate">The business-local day the appointment belongs to, as the shop names it.
/// Sent alongside the instants because "which day is this?" is zone-dependent, and a customer in
/// another zone reading only <see cref="StartsAt"/> could reasonably compute a different date than
/// the one the shop has written in its diary.</param>
public sealed record BookingConfirmedResponse(
    Guid BookingId,
    Guid WorkerId,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    DateOnly LocalDate);
