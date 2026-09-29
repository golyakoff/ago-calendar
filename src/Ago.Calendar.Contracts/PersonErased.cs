namespace Ago.Calendar.Contracts;

/// <summary>
/// `26-275`/`adr/0189`: the calendar-initiated cascade half of full person erasure. Unlike
/// <see cref="PersonRegistered"/> - the calendar minting an id chat did not yet know about - this
/// event runs in the opposite direction: **the calendar decides erasure is allowed** (it is the only
/// product that can, since the future-bookings guard reads a rule-8 fact - live event state - that
/// only this product's own database holds) and tells chat, the person's own owner (`adr/0184`), to
/// cascade the erasure to everything it owns: the Person row, and per the author's decision on
/// issue 1815, that person's conversations, messages and history - "a real right-to-be-forgotten,"
/// not a calendar-only "forget."
///
/// <para><b>Published from inside the same transaction as the calendar's own erasure</b> (CLAUDE.md
/// rule 4) - <see cref="Ago.Calendar.Application.Abstractions.IPersonEraseStore"/>'s own remarks: the
/// person's <c>PersonRecord</c> row is gone and this person's past events are anonymised before the
/// outbox row is even staged, so a reader who sees this message on the wire can trust the calendar's
/// own side already happened. Redelivery is harmless - the chat-side consumer (`26-275` slice #3) is
/// idempotent on <see cref="PersonId"/> (CLAUDE.md rule 5): a Person, its conversations and its
/// messages can each be erased at most once, and a second delivery finds nothing left to do.</para>
///
/// <para><b>No booking details, the identical reasoning <see cref="PersonRegistered"/>'s own remarks
/// give for itself</b> - carrying which bookings existed would not help chat erase a Person, and this
/// event is an instruction to erase an identity, not a report about a booking.</para>
///
/// <para><b>Wire shape, shared by copy</b> - the identical arms-length convention
/// <see cref="PersonRegistered"/>'s own remarks state: <c>ago-chat</c> declares its own
/// <c>PersonErasedWireContract</c> with these exact property names, PascalCase
/// (<see cref="System.Text.Json.JsonSerializer"/>'s default options). Changing a field here changes
/// it there too, deliberately by hand.</para>
/// </summary>
/// <param name="PersonId">The id being erased - the same opaque id <see cref="PersonRegistered.PersonId"/>
/// carries, and the one chat's own Person row is keyed by (`adr/0184`).</param>
/// <param name="AccountId">The account (chat's <c>site_id</c>, this product's <c>tenant_id</c> -
/// `adr/0093` made them the same value) the person belonged to - carried so a redelivered or
/// out-of-order message can never be applied against the wrong account's own Person.</param>
public sealed record PersonErased(
    Guid PersonId,
    Guid AccountId,
    DateTimeOffset OccurredAt,
    Guid CorrelationId);
