using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.UseCases.Tenancies;

/// <summary>
/// `22-14`/`adr/0100`: "which tenants may I act in here", asked by the console before it can offer a
/// choice.
///
/// <para><b>An <see cref="Domain.OperatorId"/> and no <see cref="TenantId"/>, deliberately.</b> Every
/// other query in this product carries both, because every other one acts on a tenant's data and the
/// permission check is scoped to it. This one is about the caller themselves - the answer <i>is</i>
/// the set of tenants - so there is no tenant to scope to and nothing for a caller to name wrongly.
/// The id is derived from the validated token's own <c>sub</c> at the endpoint
/// (<see cref="Domain.OperatorId.FromExternalSubjectId"/>), never read off the request.</para>
/// </summary>
public readonly record struct ListMyTenancies(OperatorId OperatorId);
