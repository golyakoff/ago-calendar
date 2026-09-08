namespace Ago.Calendar.Domain;

/// <summary>
/// The lead card: what a tenant accumulates about a person who books, keyed by phone number within
/// that tenant. No account, no password, no login - the product spec is explicit that the customer
/// never authenticates, and equally explicit that they are not anonymous to the business.
///
/// <para><b>Tenant-scoped identity, on purpose.</b> The same phone number booking at two different
/// shops is two lead cards, not one shared person: the notes one tenant writes about a customer are
/// that tenant's data and must never surface in another's console. The uniqueness that backs this is
/// <c>(tenant_id, phone)</c>, never <c>phone</c> alone.</para>
///
/// <para><b>Personal data.</b> This is the only entity in this product that describes a natural
/// person: a phone number, optionally a name, and free-text notes an operator typed. All three are
/// recorded in <c>ago-root/docs/architecture/personal-data.md</c>, with what removes them.</para>
/// </summary>
public sealed class Customer
{
    private const int MaxNotesLength = 4000;

    public CustomerId Id { get; }

    public TenantId TenantId { get; }

    public PhoneNumber Phone { get; }

    /// <summary>`23-59`/`adr/0147`: where this row came from - <see cref="CustomerSource.Booking"/>
    /// for the ordinary path (the default, and every row that existed before this item), and
    /// <see cref="CustomerSource.Chat"/> for one carried over from a chat contact. Never used to
    /// merge - see <see cref="SourceContactId"/>'s own remarks for why a phone matching an existing
    /// customer still becomes its own row.</summary>
    public CustomerSource Source { get; private set; }

    /// <summary>`23-59`/`adr/0147`: the originating `Ago.Chat.Domain.VisitorContactDetail`'s own id,
    /// carried opaque - this product has no reason to know anything else about that row, the same
    /// arms-length treatment `Operator.ExternalSubjectId`-style external identifiers get elsewhere in
    /// this codebase. <see langword="null"/> for a <see cref="CustomerSource.Booking"/> row.
    ///
    /// <para><b>The idempotency key for a chat-sourced row, not the phone.</b> A chat contact detail is
    /// written once and never edited (`Ago.Chat.Domain.VisitorContactDetail`'s own remarks), so this id
    /// names exactly one fact - which is what lets <c>ux_customers_tenant_source_contact</c> (a partial
    /// unique index, active only when this column is not null) make redelivering the identical
    /// <c>ContactCollected</c> event an upsert onto the <em>same</em> row rather than a duplicate, while
    /// two <em>different</em> chat contacts that happen to share a phone number still become two
    /// separate rows - the identical "a phone is a hint, not proof" reasoning `adr/0147` gives for never
    /// merging a chat-sourced row into a booking-sourced one, applied here between two chat-sourced rows
    /// as well, deliberately: nothing about this column ever collapses two distinct people who happen to
    /// share a number.</para></summary>
    public Guid? SourceContactId { get; private set; }

    /// <summary>Optional: the customer types a phone number and nothing else, and an operator fills
    /// the name in later - or never.</summary>
    public string? DisplayName { get; private set; }

    /// <summary>Free text an operator wrote about this customer.</summary>
    public string? Notes { get; private set; }

    /// <summary>How many times this customer failed to turn up. A count rather than a flag: the
    /// product spec names "prepayment required for customers with a no-show history" as the future
    /// rule this feeds, and one strike is not the same thing as five. Written by whoever marks an
    /// <see cref="Event"/> as <see cref="EventStatus.NoShow"/> (`20-04`).</summary>
    public int NoShowCount { get; private set; }

    public DateTimeOffset FirstSeenAt { get; }

    public DateTimeOffset LastSeenAt { get; private set; }

    /// <summary>
    /// `20-09`: when this phone number was first proven reachable by whoever books with it, or
    /// <see langword="null"/> if it never has been. This aggregate's own C# writers - <see cref="Register"/>
    /// and <see cref="RecordVerifiedPhone"/> - are never actually called in production; the real,
    /// load-bearing write is <c>Ago.Calendar.Infrastructure.Postgres.BookingStore</c>'s own raw SQL
    /// upsert (that type's own remarks), the identical "the domain method is the precondition's
    /// canonical statement, the SQL is what runs" split <see cref="Event.Claim"/> already has with
    /// <c>ClaimSlotSql</c>. Kept here so the domain model stays an honest description of the rule -
    /// nothing about it is *about* an <see cref="Event"/>, so it is not stated as a precondition on
    /// <see cref="Event.Claim"/>, which never inspects a customer's own fields.
    /// </summary>
    public DateTimeOffset? PhoneVerifiedAt { get; private set; }

    /// <summary>
    /// `23-12`/`decisions.md` §5: "I called and it is them" - a different fact from
    /// <see cref="PhoneVerifiedAt"/>'s own SMS-code answer, and recorded separately rather than
    /// folded into it. §5's own denominator for the reveal count: forty revealed and thirty-one
    /// confirmed is work, forty revealed and two confirmed is a question - and it can only ever be set
    /// by an operator who could see the number to call it, which is what
    /// <c>ConfirmOperatorVerifiedPhoneHandler</c>'s own <c>Permission.CustomerRead</c> gate enforces
    /// (§5: "it lives only on rungs one and two - somebody who cannot see a number cannot confirm it
    /// by calling"). Earliest-wins and idempotent, the identical shape <see cref="RecordVerifiedPhone"/>
    /// already uses for the same reason: a second confirmation is not a stronger fact than the
    /// first.</summary>
    public DateTimeOffset? PhoneConfirmedByOperatorAt { get; private set; }

    private Customer(CustomerId id, TenantId tenantId, PhoneNumber phone, CustomerSource source, Guid? sourceContactId, DateTimeOffset now)
    {
        Id = id;
        TenantId = tenantId;
        Phone = phone;
        Source = source;
        SourceContactId = sourceContactId;
        FirstSeenAt = now;
        LastSeenAt = now;
    }

    // EF Core materialization only - never called by domain code.
    private Customer()
    {
    }

    public static Customer Register(CustomerId id, TenantId tenantId, PhoneNumber phone, DateTimeOffset now) =>
        new(id, tenantId, phone, CustomerSource.Booking, sourceContactId: null, now);

    /// <summary>
    /// `23-59`/`adr/0147`: the C#-callable statement of what a chat-carried customer is - the same
    /// "the domain method is the precondition's canonical statement, the SQL is what runs" split
    /// <see cref="PhoneVerifiedAt"/>'s own remarks describe for <see cref="Register"/>/<see cref="RecordVerifiedPhone"/>:
    /// the real, load-bearing write for this factory is <c>Ago.Calendar.Infrastructure.Postgres.ContactCollectedCustomerStore</c>'s
    /// own raw SQL upsert, chosen for the identical contention reason <c>BookingStore</c>'s own remarks
    /// give (Postgres arbitrating an <c>ON CONFLICT</c> in one round trip rather than a read-then-insert
    /// a concurrent redelivery could race). Kept here so the aggregate stays an honest description of the
    /// rule regardless.
    /// </summary>
    public static Customer RegisterFromChat(
        CustomerId id, TenantId tenantId, PhoneNumber phone, Guid sourceContactId, DateTimeOffset now) =>
        new(id, tenantId, phone, CustomerSource.Chat, sourceContactId, now);

    /// <summary>What an operator edits on the card. Blank clears the field rather than being
    /// rejected - "I typed the wrong name" needs an undo, and a validator that forbids empty would
    /// make the only way to fix it a database edit.</summary>
    public void Describe(string? displayName, string? notes)
    {
        if (notes is { Length: > MaxNotesLength })
        {
            throw new ArgumentOutOfRangeException(
                nameof(notes), notes.Length, $"Notes are capped at {MaxNotesLength} characters.");
        }

        DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
        Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
    }

    /// <summary>`20-09`: the domain's own canonical statement of "once verified, stays verified" -
    /// idempotent and earliest-wins, the identical rule <c>BookingStore</c>'s own SQL <c>COALESCE</c>
    /// applies at the row level (that type's own remarks on why a later, different timestamp must never
    /// silently replace an earlier one).</summary>
    public void RecordVerifiedPhone(DateTimeOffset verifiedAt) => PhoneVerifiedAt ??= verifiedAt;

    /// <summary>`23-12`: the domain's own canonical statement of "once an operator has confirmed it by
    /// calling, that stands" - see <see cref="PhoneConfirmedByOperatorAt"/>'s own remarks for why this
    /// is a distinct fact from <see cref="RecordVerifiedPhone"/> rather than a second caller of
    /// it.</summary>
    public void RecordOperatorConfirmedPhone(DateTimeOffset confirmedAt) => PhoneConfirmedByOperatorAt ??= confirmedAt;

    public void RecordNoShow(DateTimeOffset now)
    {
        NoShowCount++;
        Touch(now);
    }

    /// <summary>Moves the "last seen" watermark forward and never backward - a late-arriving write
    /// must not rewind it, and time is a parameter here precisely so that rule is testable.</summary>
    public void Touch(DateTimeOffset now)
    {
        if (now > LastSeenAt)
        {
            LastSeenAt = now;
        }
    }
}
