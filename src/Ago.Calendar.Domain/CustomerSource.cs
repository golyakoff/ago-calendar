namespace Ago.Calendar.Domain;

/// <summary>
/// `23-59`/`adr/0147`: where a <see cref="Customer"/> row's own existence comes from - the mark the
/// backlog item's own Done-when requires ("a phone matching an existing customer creates a separate
/// row, marked as having come from chat"). Two members, not a boolean: a boolean answers "is this
/// from chat", which reads exactly as well until a third source exists and the type has to be
/// widened anyway - the same "closed vocabulary, not a flag" reasoning
/// <c>Ago.Chat.Domain.VisitorContactDetailSource</c> already applies for the identical shape on the
/// chat side of this same item.
///
/// <para>Stored as the CLR member name via EF's default string conversion, not an ordinal - the same
/// "an ordinal makes reordering this enum a silent data corruption" reasoning that chat-side type's
/// own remarks give for itself.</para>
/// </summary>
public enum CustomerSource
{
    /// <summary>The ordinary path: a phone number arriving through <c>BookingStore</c>'s own upsert
    /// when someone books. Every <see cref="Customer"/> row before this item existed is this
    /// source.</summary>
    Booking,

    /// <summary>`23-59`/`adr/0147`: carried over from a contact AGO Chat collected - live, through
    /// <c>ContactCollectedConsumer</c>, or retroactively, through <c>ago-chat</c>'s own
    /// <c>ContactCarryoverBackfill</c>. Never merged into a <see cref="Booking"/>-sourced row for the
    /// same phone - that merge is `23-60`'s own deliberate act, not this source's job.</summary>
    Chat,
}
