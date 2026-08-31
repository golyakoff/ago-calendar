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

    private Customer(CustomerId id, TenantId tenantId, PhoneNumber phone, DateTimeOffset now)
    {
        Id = id;
        TenantId = tenantId;
        Phone = phone;
        FirstSeenAt = now;
        LastSeenAt = now;
    }

    // EF Core materialization only - never called by domain code.
    private Customer()
    {
    }

    public static Customer Register(CustomerId id, TenantId tenantId, PhoneNumber phone, DateTimeOffset now) =>
        new(id, tenantId, phone, now);

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
