namespace Ago.Calendar.Contracts;

/// <summary>
/// <b>The answer to `ago-chat`'s own question.</b> `23-88`/`adr/0165`: chat asks, over its own outbox,
/// how many of a tenant's own countable things a candidate quantity would exceed
/// (<c>Ago.Chat.Contracts.ModuleQuantityImpactRequested</c>); this is the calendar's own reply, on the
/// calendar's own outbox, to the literal topic <c>"ModuleQuantityImpactComputed"</c> -
/// <c>Ago.Chat.Worker.ModuleQuantityImpactComputedConsumer</c> already subscribes to it and is ready to
/// apply it (that consumer's own remarks, shipped ahead of any publisher in the `ago-chat` half of this
/// item).
///
/// <para><b>The first calendar-to-chat crossing</b> (`adr/0165`) - every other integration event this
/// product has ever published or consumed ran chat-to-calendar. The direction is new; the mechanism
/// (outbox in, outbox out, `adr/0027`'s own product-independence guarantee preserved) is not.</para>
///
/// <para><b>A read-only reply - nothing this product writes to answer it.</b> Unlike
/// <c>ModuleQuantityGranted</c>, which the calendar applies inside a locked, transactional
/// read-decide-write (`adr/0125`'s own <c>WorkerQuotaPolicy</c>/<c>IWorkerQuotaGrantStore</c>), this
/// event only reports what <i>would</i> happen - <see cref="WorkerQuotaPolicy.SelectWorkersToDeactivate"/>
/// run against a live, unlocked read. No lock is needed because nothing here is a decision the
/// calendar has committed to: the owner may never confirm, and even if they do,
/// `ModuleQuantityGrantedConsumer`'s own transaction recomputes the real answer fresh at that later,
/// separate moment (CLAUDE.md rule 8 - a write decision never trusts a cached read, and this reply is
/// exactly such a cache, by design, on chat's own side).</para>
///
/// <para><see cref="AffectedItemDisplayNames"/> is opaque to chat the same way
/// <c>ModuleQuantityGranted</c>'s own quantity is - chat never learns these are worker names rather
/// than some other module's own countable thing.</para>
/// </summary>
/// <param name="SiteId">`22-03`: the calendar's own tenancy row equals the account id, so this is
/// both chat's <c>SiteId</c> and this product's own <see cref="Domain.TenantId"/> unconverted.</param>
/// <param name="ModuleKey">Always the literal <c>"calendar"</c> from this publisher - carried anyway
/// so a consumer subscribing to one topic across every answering module never has to guess who
/// answered.</param>
/// <param name="RequestedQuantity">Echoed back from the question, unchanged - the candidate quantity
/// this answer is about, not whatever is currently granted.</param>
/// <param name="AffectedCount"><see cref="AffectedItemDisplayNames"/>'s own count, carried
/// separately so a consumer that only needs the number never has to deserialize the list.</param>
/// <param name="AffectedItemDisplayNames">Newest-created first, the identical order
/// <see cref="WorkerQuotaPolicy.SelectWorkersToDeactivate"/> already returns for the real
/// deactivation - so a preview a tenant reads names the same workers, in the same order, that a
/// confirmed downgrade would actually deactivate.</param>
/// <param name="CorrelationId">Threaded through from the question this answers, unlike
/// <c>BookingConfirmed</c>'s own fresh id - the request's own correlation id is available here and
/// threading it is what actually correlates the two halves of this round trip in a trace.</param>
/// <param name="OccurredAt">When the calendar computed this answer, not when the question was
/// asked.</param>
public sealed record ModuleQuantityImpactComputed(
    Guid SiteId,
    string ModuleKey,
    int RequestedQuantity,
    int AffectedCount,
    IReadOnlyList<string> AffectedItemDisplayNames,
    Guid CorrelationId,
    DateTimeOffset OccurredAt);
