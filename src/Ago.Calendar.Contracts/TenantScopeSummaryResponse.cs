namespace Ago.Calendar.Contracts;

/// <summary>
/// `24-17`: `GET /api/v1/owner/tenant-isolation`'s response body - this product's own analogue of
/// `Ago.Chat.Contracts.TenantIsolationSummaryResponse`, deliberately narrower.
///
/// <para><b>No `ExemptListed`/`UnaccountedKeys` here - read `NotGatedKeys` as a weaker, unclassified
/// fact, not as this product's `Unaccounted`.</b> `Ago.Calendar.Application.Abstractions.TenantScopeSnapshot`'s
/// own remarks say why the split `ago-chat`'s response makes does not exist for this product: nobody
/// has ever built the reasoned exemption catalogue that split depends on. A non-empty
/// <see cref="NotGatedKeys"/> here is <b>not</b> the same finding a non-empty `UnaccountedKeys` is on
/// the `ago-chat` side of the console screen - it mixes legitimate consumer/worker-side handlers and
/// the unauthenticated public-booking surface in with anything genuinely unreviewed, indistinguishably,
/// because no reviewer has ever separated them. Treat a long list here as "unclassified", not
/// "wrong".</para>
/// </summary>
/// <param name="EntryPoints">Every public method of every `*Handler` class in
/// `Ago.Calendar.Application.UseCases`.</param>
/// <param name="HandlerClasses">How many distinct handler classes those entry points come from.</param>
/// <param name="RbacGated">Entry points that take a `TenantId` and call `IPermissionChecker`.</param>
/// <param name="NotGated">`EntryPoints - RbacGated`.</param>
/// <param name="NotGatedKeys">Those entry points, unclassified - see this record's own remarks.</param>
/// <param name="RoutesAndHubMethods">HTTP routes (excluding `GET /healthz/version`) plus
/// `CalendarOperatorHub`'s own public methods - zero today, since that hub is push-only
/// (`CalendarOperatorHub`'s own remarks), read live from this process's route table and hub type
/// rather than assumed.</param>
/// <param name="ClientSuppliedTenantIdRoutes">Of those, how many resolve to a path containing a
/// literal <c>{tenantId}</c> route segment.</param>
/// <param name="GeneratedAtUtc">When this snapshot was computed - never cached, always "just
/// now".</param>
public sealed record TenantScopeSummaryResponse(
    int EntryPoints,
    int HandlerClasses,
    int RbacGated,
    int NotGated,
    IReadOnlyList<string> NotGatedKeys,
    int RoutesAndHubMethods,
    int ClientSuppliedTenantIdRoutes,
    DateTimeOffset GeneratedAtUtc);
