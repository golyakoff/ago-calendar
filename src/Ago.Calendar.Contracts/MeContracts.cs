namespace Ago.Calendar.Contracts;

/// <summary>
/// `22-14`/`adr/0100`: the wire shape of <c>GET /api/v1/me/tenancies</c> - routes about the calling
/// <i>identity</i> rather than about an already-resolved tenant, and the first of them in this
/// product. Its own file for the same reason `ago-chat`'s <c>MeEndpoints</c> is its own file: it is
/// reachable by a caller who has no <c>tenant_id</c> claim yet, which is the opposite of everything
/// in <see cref="CreateCalendarRequest"/>'s file.
///
/// <para><b>Field names, not `ago-chat`'s.</b> That product answers the same question with
/// <c>siteId</c>/<c>siteName</c>; this one says <c>tenantId</c>/<c>tenantName</c>, because a reader of
/// this API sees tenants and there are no sites here. The <i>values</i> are the same ids - `adr/0093`
/// unified tenancy across the two products, and <c>RoleAssignmentsChangedConsumer</c> maps one onto
/// the other - which is exactly why the console sends one header
/// (<c>X-Ago-Active-Site</c>) to both backends rather than two.</para>
/// </summary>
/// <param name="TenantName">Possibly empty - see <c>Ago.Calendar.Application.Abstractions.TenancyRow</c>
/// for the state that produces it and why such a tenancy is listed rather than dropped. A client
/// renders its own fallback, the same way `ago-console`'s existing switcher already does for a site
/// with a blank name.</param>
public sealed record TenancyResponse(Guid TenantId, string TenantName);

/// <param name="Tenancies">Empty for an authenticated identity this product has never heard of - a
/// true answer, not an error. That is the shape the console needs to distinguish "you have no
/// calendar anywhere" from "you have one, in a different shop than the one you are looking at",
/// which is the distinction `22-14` exists to make possible at all.</param>
public sealed record TenanciesResponse(IReadOnlyList<TenancyResponse> Tenancies);
