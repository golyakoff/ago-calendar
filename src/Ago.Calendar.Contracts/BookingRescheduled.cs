namespace Ago.Calendar.Contracts;

/// <summary>
/// `26-208`/`adr/0187`: <b>this booking moved to a new time.</b> A past-tense fact, published after the
/// reschedule transaction it describes has committed (CLAUDE.md rule 4) - the operator cancelled the
/// old run and claimed a new one, same worker and same service, in one transaction.
///
/// <para><b>One clean event carrying the old->new link, not a <c>Cancelled</c> + fresh
/// <see cref="BookingConfirmed"/> pair.</b> This is the deliberate departure `adr/0187` §Decision
/// makes from the ordinary confirm path. A reschedule expressed as the default cancel-plus-new-booking
/// pair is, in the raw event stream retained from day one (`adr/0186`), indistinguishable from a real
/// cancellation followed by a brand-new booking - and a report correlating the two after the fact is
/// fragile enough to miscount. A dedicated event with a mandatory <see cref="PreviousEventId"/> /
/// <see cref="PreviousStartsAt"/> link keeps the retained analytics honest before any consumer is
/// built. The eventual readers are `20-05`'s SMS consumer ("your appointment moved from X to Y") and
/// the `ago-analytics` ingest; <b>this item builds neither</b>. The old->new link is what makes a
/// future reschedule-chain-root id (`adr/0187` §Deferred) reconstructable, and it is mandatory here.
///
/// <para><b>What is NOT staged alongside it.</b> The reschedule stages exactly this one event. It does
/// not stage the <see cref="BookingConfirmed"/> / <c>BookingPendingStateChanged("Booked")</c> pair the
/// ordinary confirm path stages for the new run (the new run lands straight in
/// <c>Booked</c> - an operator placing an appointment is the confirmation authority, `adr/0187`), and
/// it does not stage a <c>Cancelled</c> signal for the old run. One reschedule, one event.</para>
/// </summary>
/// <param name="EventId">The <b>new</b> booking's own anchor id (<c>Event.BookingId</c> of the freshly
/// claimed run). Booking identity changes across a reschedule (`adr/0187` §Consequences); this is the
/// id the moved booking now has, and the identity a consumer keys on going forward - the same "the
/// anchor id is the booking's identity" meaning <see cref="BookingConfirmed.EventId"/> carries.</param>
/// <param name="PreviousEventId">The <b>old</b> booking's anchor id, now <c>Cancelled</c>. The
/// mandatory old->new link - without it the retained stream cannot tell this move from a cancel plus a
/// new booking (see the type's own remarks).</param>
/// <param name="TenantId">Whose booking moved. Carried on the event for the same reason
/// <see cref="BookingConfirmed.TenantId"/> is - a consumer must not join to learn which tenant a
/// message concerns.</param>
/// <param name="CalendarId">Resolves the IANA zone a human-readable time is rendered in (adr/0049),
/// the same reason <see cref="BookingConfirmed.CalendarId"/> is present.</param>
/// <param name="PersonId">`adr/0184`: the opaque person id - unchanged by the move, since a reschedule
/// carries the same person onto the new run. Resolves the phone at send time; never a copied
/// snapshot, the same rule <see cref="BookingConfirmed.PersonId"/> follows.</param>
/// <param name="PreviousStartsAt">When the old booking was, ISO-8601 with an explicit offset, UTC on
/// the wire (date-and-time.md). What an SMS renders as the "was" half of "moved from X to Y".</param>
/// <param name="NewStartsAt">When the booking is now. The "to" half.</param>
/// <param name="NewEndsAt">The new run's own end, exclusive - the whole run's last slot's end, buffers
/// between the run's slots included, matching <see cref="BookingConfirmed.EndsAt"/>'s own group span.</param>
/// <param name="LocalDate">The new booking's business-local day, as the shop names it - stored rather
/// than derived (adr/0049) so no consumer re-derives it.</param>
/// <param name="OccurredAt">When the reschedule committed, not when either visit is.</param>
/// <param name="CorrelationId">A fresh id minted at the transition - the same "an operator action mints
/// its own correlation" reasoning <see cref="BookingConfirmed.CorrelationId"/> gives.</param>
public sealed record BookingRescheduled(
    Guid EventId,
    Guid PreviousEventId,
    Guid TenantId,
    Guid CalendarId,
    Guid PersonId,
    DateTimeOffset PreviousStartsAt,
    DateTimeOffset NewStartsAt,
    DateTimeOffset NewEndsAt,
    DateOnly LocalDate,
    DateTimeOffset OccurredAt,
    Guid CorrelationId);
