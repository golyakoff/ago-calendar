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
/// </summary>
public sealed class Service
{
    private const int MaxDurationMinutes = 12 * 60;

    /// <summary>Long enough for real marketing copy ("what this includes, how to prepare") and short
    /// enough that a service list stays a list - the same order of magnitude as
    /// <see cref="Customer.Notes"/>'s own 4000, chosen smaller because this text is read by a stranger
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
