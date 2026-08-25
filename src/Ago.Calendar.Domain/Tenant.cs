namespace Ago.Calendar.Domain;

/// <summary>
/// The account holder - a shop, a salon, a clinic. Structurally what <c>Site</c> is in AGO Chat, and
/// deliberately not that row: the two products own separate databases (adr/0027), so this is a new
/// top-level entity rather than a borrowed one.
///
/// <para>A tenant configures and never participates. It books nothing, confirms nothing and appears
/// in no <see cref="Event"/>; the day-to-day actor is <see cref="Operator"/>, and the person a
/// customer meets is <see cref="Worker"/>. In a one-person business all three are the same human
/// being, which is exactly why they have to be three types - the model must let them coincide
/// without forcing them to.</para>
/// </summary>
public sealed class Tenant
{
    public TenantId Id { get; }

    public string Name { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; }

    private Tenant(TenantId id, string name, DateTimeOffset now)
    {
        Id = id;
        Name = name;
        CreatedAt = now;
    }

    // EF Core materialization only - never called by domain code.
    private Tenant()
    {
    }

    public static Tenant Register(TenantId id, string name, DateTimeOffset now)
    {
        // clean-architecture.md: "there is no such thing as a validated-somewhere-else entity". The
        // alternative - a FluentValidation rule in the Application layer - would leave the aggregate
        // constructible in a state the aggregate itself calls illegal, which is the whole failure
        // mode this rule exists to prevent.
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new Tenant(id, name.Trim(), now);
    }

    public void Rename(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name.Trim();
    }
}
