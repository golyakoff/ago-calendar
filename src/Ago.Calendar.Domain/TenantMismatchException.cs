namespace Ago.Calendar.Domain;

/// <summary>
/// An attempt to relate two entities belonging to different tenants - a worker joining another
/// tenant's calendar, an operator granted another tenant's role. Multi-tenancy is an invariant here,
/// not a <c>WHERE</c> clause somebody remembers to add: this exception is what makes "a worker
/// belongs to exactly one tenant" a rule an aggregate enforces rather than a sentence in a document.
/// </summary>
public sealed class TenantMismatchException(string message) : InvalidOperationException(message);
