using Ago.Calendar.Domain;

namespace Ago.Calendar.Infrastructure.Postgres.Persistence;

/// <summary>
/// `22-05`/`adr/0093`: EF-mapped shape of the `role_assignment_projections` table - a plain
/// persistence record, not a Domain entity, matching the same "nothing above the checker manages this
/// data, so there is nothing for a richer model to buy" judgement `Ago.Chat`'s own <c>RoleRecord</c>
/// makes for its identical reason. Composite-keyed on <c>(OperatorId, TenantId)</c>, the pair the row
/// is a fact about - no surrogate key, the same "a join fact needs no identity of its own" shape
/// <c>RoleAssignment</c> used to draw before this item removed it.
/// </summary>
internal sealed class RoleAssignmentProjectionRecord
{
    public OperatorId OperatorId { get; set; }

    public TenantId TenantId { get; set; }

    /// <summary>Kept alongside the derived <see cref="OperatorId"/> for the one thing a Guid cannot
    /// answer on its own - which raw subject this row is about, useful reading the table by hand and
    /// for a future audit trail. Never used to derive <see cref="OperatorId"/> back
    /// (<c>Ago.Calendar.Domain.OperatorId.FromExternalSubjectId</c> only ever runs forward).</summary>
    public string ExternalSubjectId { get; set; } = string.Empty;

    public List<string> Permissions { get; set; } = [];

    public DateTimeOffset UpdatedAt { get; set; }
}
