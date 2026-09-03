namespace Ago.Calendar.Domain;

/// <summary>
/// An attempt to relate two entities belonging to different tenants - a worker joining another
/// tenant's calendar, a working-hours rule attached to another tenant's calendar. Multi-tenancy is an
/// invariant here, not a <c>WHERE</c> clause somebody remembers to add: this exception is what makes
/// "an entity belongs to exactly one tenant" a rule an aggregate enforces rather than a sentence in a
/// document.
///
/// <para><b>`22-05`/`adr/0093`: no longer thrown by <c>Operator.Grant</c>/<c>Revoke</c></b> - both
/// methods are gone along with the aggregate they belonged to. <see cref="Worker"/> and
/// <see cref="WorkingHoursRule"/> still throw it for their own, unrelated cross-tenant checks.</para>
/// </summary>
public sealed class TenantMismatchException(string message) : InvalidOperationException(message);
