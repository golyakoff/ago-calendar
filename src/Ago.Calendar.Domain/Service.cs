namespace Ago.Calendar.Domain;

/// <summary>
/// Something a worker does for a customer, with a duration: "haircut, 45 minutes". The duration is
/// what sets a materialised slot's length (`20-02`).
///
/// <para><b>Whole minutes, deliberately.</b> A slot boundary that lands on a fraction of a minute is
/// unreadable in every UI that renders it and impossible to type back in. Storing the duration as an
/// <c>int</c> of minutes rather than a Postgres <c>interval</c> also keeps the column trivially
/// comparable and orderable in SQL; the CLR side stays a <see cref="TimeSpan"/>, because that is the
/// type the rest of the domain does arithmetic in (date-and-time.md rule 7).</para>
///
/// <para>v1 books one service per visit. Two services in one appointment ("haircut and beard") is
/// deferred by the product spec, with an explicit workaround - two adjacent slots - and it is worth
/// noting that nothing here forecloses it: a booking that spans several slots is a change to
/// <see cref="Event"/>'s claim path, not to this type.</para>
///
/// <para><b>`23-35`: a price and a description, both optional, independently.</b> A service with
/// neither is exactly as valid as one with both - a nullable field that is sometimes wrong is worse
/// than an honest absence, and a shop still setting itself up, or one that never wants to state a
/// price at all, is a real and common state, not a validation gap to close.</para>
///
/// <para><b><see cref="PriceIsFrom"/> is what answers "what does a price mean when the real cost
/// depends on the master or on how long the job actually takes".</b> Rather than picking one universal
/// reading - always exact, or always a floor - the operator states which this particular service's
/// number is. A fixed-price consultation and a haircut priced by hair length both fit on this one
/// field: the first leaves it <see langword="false"/>, the second sets it <see langword="true"/> and
/// every renderer prefixes the number with "от" ("from"). <see cref="PriceIsFrom"/> is meaningless
/// while <see cref="Price"/> is <see langword="null"/> and is normalised to <see langword="false"/>
/// in that case, so the two fields can never disagree about whether there is a price to qualify.</para>
///
/// <para><b>`26-96`: <see cref="IsActive"/>, and why this product archives a service rather than
/// deleting one.</b> Until this item a service could be created and never corrected or withdrawn -
/// a typo in a duration or a price was permanent, and that price is what a stranger reads on the
/// booking widget before booking. Correcting it is <see cref="Reconfigure"/>. Withdrawing it is this
/// flag, not a <c>DELETE</c>, and the reason is in the schema rather than in taste: a booking row
/// keeps <c>events.service_id</c> forever (cancelled and no-show rows included), and four read
/// models resolve a booking's *service name* through <c>left join services s on s.id =
/// e.service_id</c> - <c>PendingBookingReadStore</c>, <c>ConfirmedBookingReadStore</c>,
/// <c>WorkerSlotReadStore</c>. Deleting the row would blank
/// the service name on every past booking that ever used it, retroactively, in every screen that
/// renders one. Refusing the delete instead ("option (b)") sounds safer and is worse: because those
/// rows are never purged, a service booked even once could then never be withdrawn at all, which is
/// the exact gap this item exists to close. So: the row stays, <see cref="IsActive"/> says whether
/// it is still on offer, and the two surfaces read it in opposite directions - the console's own
/// configuration read returns archived services (so a worker card and a booking can still name
/// theirs), while the public booking surface and the claim path refuse them.</para>
///
/// <para><b>A boolean, not an <c>archivedAt</c> timestamp</b> - <see cref="Worker.IsActive"/> is
/// already exactly this concept on the sibling aggregate, rendered in the same console next to this
/// one; a second spelling for one idea would make "inactive" mean two shapes on one screen. A
/// timestamp would also claim to record *when*, and a re-archived service would overwrite the first
/// answer, so it would be a worse record, not a richer one.</para>
/// </summary>
public sealed class Service
{
    private const int MaxDurationMinutes = 12 * 60;

    /// <summary>Long enough for real marketing copy ("what this includes, how to prepare") and short
    /// enough that a service list stays a list - the same order of magnitude as
    /// the 4000 a free-text note gets elsewhere in this suite, chosen smaller because this text is read by a stranger
    /// deciding whether to book, not by an operator who already knows the shop.</summary>
    private const int MaxDescriptionLength = 1000;

    public ServiceId Id { get; }

    public TenantId TenantId { get; }

    public string Name { get; private set; } = string.Empty;

    public TimeSpan Duration { get; private set; }

    /// <summary>Freeform text the tenant maintains, or <see langword="null"/> when nobody has written
    /// one yet - never an empty string (see <see cref="ValidateDescription"/>).</summary>
    public string? Description { get; private set; }

    /// <summary>The commercial promise this service is shown with, or <see langword="null"/> when the
    /// tenant deliberately states none. See the type's own remarks and <see cref="PriceIsFrom"/> for
    /// what "no price" and "a price that varies" each mean here.</summary>
    public Money? Price { get; private set; }

    /// <summary>Meaningful only when <see cref="Price"/> is not <see langword="null"/> - see the
    /// type's own remarks.</summary>
    public bool PriceIsFrom { get; private set; }

    /// <summary>`26-96`. An archived service keeps every row that references it - a worker still
    /// lists it, a past booking still resolves its name through it - and simply stops being offered:
    /// the public booking surface does not list it, and <c>BookEventHandler</c> refuses a claim that
    /// names it. Reversible by design (<see cref="Reactivate"/>): a seasonal service withdrawn in
    /// October comes back in May, and re-creating it would produce a second row with a second id that
    /// every historical booking would still not point at.</summary>
    public bool IsActive { get; private set; }

    private Service(
        ServiceId id, TenantId tenantId, string name, TimeSpan duration,
        Money? price, bool priceIsFrom, string? description)
    {
        Id = id;
        TenantId = tenantId;
        Name = name;
        Duration = duration;
        Price = price;
        PriceIsFrom = price is null ? false : priceIsFrom;
        Description = description;
        IsActive = true;
    }

    // EF Core materialization only - never called by domain code.
    private Service()
    {
    }

    public static Service Create(
        ServiceId id, TenantId tenantId, string name, TimeSpan duration,
        Money? price = null, bool priceIsFrom = false, string? description = null) =>
        new(
            id, tenantId, ValidateName(name), Validate(duration),
            price, priceIsFrom, ValidateDescription(description));

    /// <summary>
    /// `26-96`: the correction path, and it validates exactly what <see cref="Create"/> validates -
    /// the same three private helpers, not a second, laxer set. A duration that could never have been
    /// created must not be reachable by editing into it.
    ///
    /// <para>Deliberately silent about <see cref="IsActive"/>, which moves only through
    /// <see cref="Deactivate"/>/<see cref="Reactivate"/> - the identical split
    /// <see cref="BookingCalendar.Reconfigure"/> and <see cref="BookingCalendar.Publish"/> already
    /// draw on the sibling aggregate, and for the same reason: "correct this text" and "stop offering
    /// this" are different decisions, and a single setter taking both would let an edit form that
    /// forgot one field silently reverse the other. A caller that means both says both - see
    /// <c>UpdateServiceHandler</c>, which is one transaction over the two calls exactly as
    /// <c>UpdateCalendarHandler</c> is.</para>
    /// </summary>
    public void Reconfigure(
        string name, TimeSpan duration,
        Money? price = null, bool priceIsFrom = false, string? description = null)
    {
        Name = ValidateName(name);
        Duration = Validate(duration);
        Price = price;
        PriceIsFrom = price is null ? false : priceIsFrom;
        Description = ValidateDescription(description);
    }

    /// <summary>`26-96`: take this service out of rotation - see the type's own remarks for why this
    /// is what "delete a service" means in this product. Idempotent: archiving an already-archived
    /// service is a no-op rather than a refusal, so a retried request never fails.</summary>
    public void Deactivate() => IsActive = false;

    /// <summary>`26-96`: put it back on offer. Idempotent for the same reason
    /// <see cref="Deactivate"/> is.</summary>
    public void Reactivate() => IsActive = true;

    private static string ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return name.Trim();
    }

    private static string? ValidateDescription(string? description)
    {
        if (description is { Length: > MaxDescriptionLength })
        {
            throw new ArgumentOutOfRangeException(
                nameof(description), description.Length,
                $"A service description is capped at {MaxDescriptionLength} characters.");
        }

        return string.IsNullOrWhiteSpace(description) ? null : description.Trim();
    }

    private static TimeSpan Validate(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(duration), duration, "A service must take a positive amount of time.");
        }

        if (duration.Ticks % TimeSpan.TicksPerMinute != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(duration), duration, "A service duration is a whole number of minutes.");
        }

        if (duration > TimeSpan.FromMinutes(MaxDurationMinutes))
        {
            throw new ArgumentOutOfRangeException(
                nameof(duration), duration, $"A service must fit inside a working day ({MaxDurationMinutes} minutes).");
        }

        return duration;
    }
}
