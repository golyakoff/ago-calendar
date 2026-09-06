using Ago.Calendar.Domain;

namespace Ago.Calendar.Infrastructure.Postgres.Persistence;

/// <summary>
/// `23-12`/`adr/0123`: EF-mapped shape of the `contact_visibility_projections` table - a plain
/// persistence record, not a Domain entity, the identical "nothing above the checker manages this
/// data, so there is nothing for a richer model to buy" judgement
/// <see cref="RoleAssignmentProjectionRecord"/> already makes for the same reason. Keyed on
/// <see cref="TenantId"/> alone - <see cref="Application.Abstractions.IContactVisibilityProjectionStore"/>'s
/// own remarks on why there is no second dimension.
/// </summary>
internal sealed class ContactVisibilityProjectionRecord
{
    public TenantId TenantId { get; set; }

    /// <summary>The wire value verbatim (<c>"Visible"</c> or <c>"MaskedWithReveal"</c>), not
    /// <see cref="ContactVisibility"/> itself - a plain <c>text</c> column parsed at read time, the
    /// same "nothing here is Domain state to validate on write, only to parse on read" shape a raw
    /// projection of an external fact already takes throughout this table's own family.</summary>
    public string Rung { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; set; }
}
