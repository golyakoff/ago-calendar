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
/// </summary>
public sealed class Service
{
    private const int MaxDurationMinutes = 12 * 60;

    public ServiceId Id { get; }

    public TenantId TenantId { get; }

    public string Name { get; private set; } = string.Empty;

    public TimeSpan Duration { get; private set; }

    private Service(ServiceId id, TenantId tenantId, string name, TimeSpan duration)
    {
        Id = id;
        TenantId = tenantId;
        Name = name;
        Duration = duration;
    }

    // EF Core materialization only - never called by domain code.
    private Service()
    {
    }

    public static Service Create(ServiceId id, TenantId tenantId, string name, TimeSpan duration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new Service(id, tenantId, name.Trim(), Validate(duration));
    }

    public void Reconfigure(string name, TimeSpan duration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name.Trim();
        Duration = Validate(duration);
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
