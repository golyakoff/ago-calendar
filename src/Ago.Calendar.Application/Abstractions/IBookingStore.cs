using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Abstractions;

/// <summary>
/// The booking write, in one transaction: the customer's lead card is upserted by phone, and the
/// slot is claimed with a compare-and-set whose rows-affected count is the verdict.
///
/// <para><b>Why the claim is a raw atomic <c>UPDATE</c> and not an EF load-mutate-save.</b> This is
/// the second time this codebase makes the call (`4-01` made it for operator capacity), so the
/// reasoning is restated rather than cited. The question a booking must answer correctly is "is this
/// slot still free *at the instant I take it*". An EF aggregate write cannot express that as one
/// round trip: it reads the row, decides in memory, and writes back - and between the read and the
/// write another customer's claim can land. EF's own answer to that gap is optimistic concurrency,
/// which turns the second customer's attempt into a <c>DbUpdateConcurrencyException</c> the caller
/// must catch, interpret and retry, on the hottest and most contended path in the product. A single
/// <c>UPDATE ... WHERE id = @id AND status = 'Available'</c> has no gap to lose: Postgres evaluates
/// the predicate and applies the change under the same row lock, so of two simultaneous callers one
/// gets 1 row and the other gets 0, and neither has to know the other existed. The predicate is not
/// a value read separately and passed in - it names the same row's own column, so there is no
/// earlier read to go stale. CLAUDE.md rule 8 states the rule this follows; `6-09` is the worked
/// example of the read-then-write version losing.
///
/// <b>The alternative rejected:</b> load the <see cref="Event"/>, call
/// <see cref="Event.Claim"/>, save, and let <c>xmin</c> reject the loser. It compiles, it is more
/// idiomatic, and it is worse in three ways - a second round trip on every booking, a lost race that
/// arrives as an exception rather than as a number, and a domain method whose in-memory status check
/// looks like the guarantee when it is not. <see cref="Event.Claim"/>'s own remarks already say so.
/// It stays useful: `20-04`'s operator-driven transitions are uncontended single-actor writes where
/// load-mutate-save is exactly right.</para>
///
/// <para><b>Why the lead-card upsert is in the same transaction as the claim.</b> Not for atomicity
/// of the two writes as such - a lead card without a booking harms nothing operationally - but
/// because it is personal data. Writing a customer's phone number for a booking that then loses the
/// race would mean the endpoint accumulates identifying rows for actions that never happened, which
/// is a data-minimisation problem, not a consistency one. One transaction makes a failed claim leave
/// no trace. It also gives the pair a single ordering rule: the customer row is locked first, always,
/// so two bookings from the same number can never deadlock against each other.</para>
///
/// <para><b>One port rather than two.</b> `20-03`'s backlog names an <c>IEventClaimStore</c> holding
/// only the claim. It became this instead, because the transaction spanning both statements has to
/// belong to something, and splitting it across two ports would put the boundary in a handler that
/// cannot see it - the same argument <see cref="ITenantRepository"/> already made when it predicted
/// this product's first multi-aggregate write, and the shape AGO Chat's
/// <c>ISiteRegistrationRepository</c> settled on for the same reason.</para>
/// </summary>
public interface IBookingStore
{
    /// <summary>
    /// Upserts the lead card and attempts the claim.
    ///
    /// <para>Returns <c>null</c> when the slot was not claimable - taken by somebody else moments
    /// ago, already started, blocked, or not on the calendar the caller named. <b>That is an ordinary
    /// outcome, not a fault</b>: it is what happens to the second of two people reaching for the last
    /// table, it must never be logged at <c>Error</c>, never surface as a 500, and never be
    /// distinguished from success by an exception. `4-01` set that precedent explicitly and
    /// concurrency.md repeats it. Nothing is written when it happens - the transaction rolls
    /// back.</para>
    ///
    /// <para>The deadline is an absolute instant supplied by the caller, not a window this port adds
    /// to its own reading of the clock: time is a parameter everywhere outside Infrastructure
    /// (adr/0011), and an adapter that computed <c>now + window</c> internally would be an adapter no
    /// test could pin to a fixed instant.</para>
    /// </summary>
    Task<BookingConfirmation?> TryBookAsync(BookingAttempt attempt, CancellationToken cancellationToken);
}

/// <summary>
/// Everything one booking write needs. A record rather than eight parameters because the port's own
/// remarks are about the pair of statements, and a caller reading the call site should see which
/// facts belong to the customer and which to the slot.
/// </summary>
/// <param name="TenantId">Resolved from the calendar by the handler - never taken from the request,
/// which is unauthenticated and may claim anything.</param>
/// <param name="CalendarId">Part of the claim's own <c>WHERE</c> clause, not a pre-check: an event
/// id that belongs to another calendar is then unclaimable by construction rather than by a
/// validation somebody could forget.</param>
/// <param name="EventId">The slot being claimed.</param>
/// <param name="ServiceId">What the customer is booking. Written by the claim, since a materialised
/// slot has no service until somebody chooses one.</param>
/// <param name="Phone">The lead card's key within the tenant. Personal data - see
/// <c>personal-data.md</c>.</param>
/// <param name="DisplayName">Optional name the customer typed. Never overwrites a name an operator
/// already curated; see the adapter.</param>
/// <param name="NewCustomerId">The id to use *if* the upsert inserts. Generated by the handler
/// through <c>IIdGenerator</c>, because Domain and Application never mint ids themselves and an
/// adapter that called <c>Guid.NewGuid()</c> would be unreproducible in a test.</param>
/// <param name="Now">The instant the booking is being made, from <c>IClock</c>. Also the claim's
/// "this slot has not started yet" predicate.</param>
/// <param name="ConfirmationDeadline">When the operator's veto window closes.</param>
public readonly record struct BookingAttempt(
    TenantId TenantId,
    CalendarId CalendarId,
    EventId EventId,
    ServiceId ServiceId,
    PhoneNumber Phone,
    string? DisplayName,
    CustomerId NewCustomerId,
    DateTimeOffset Now,
    DateTimeOffset ConfirmationDeadline);

/// <summary>
/// What a successful claim returns, read back by the same statement that performed it
/// (<c>UPDATE ... RETURNING</c>) rather than by a second query. A follow-up <c>SELECT</c> would be a
/// second round trip observing a row that a later transition could already have moved on - the
/// values below are the ones the claim itself wrote, which is the only reading that is certainly
/// about this booking.
/// </summary>
public readonly record struct BookingConfirmation(
    EventId EventId,
    CustomerId CustomerId,
    WorkerId WorkerId,
    TimeSlot Slot,
    DateOnly LocalDate);
