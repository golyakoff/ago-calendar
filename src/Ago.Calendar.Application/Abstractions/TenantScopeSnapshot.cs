namespace Ago.Calendar.Application.Abstractions;

/// <summary>
/// `24-17`: `ITenantScopeInspector`'s answer for this product - deliberately a narrower shape than
/// `ago-chat`'s own `Ago.Chat.Application.Abstractions.TenantScopeSnapshot`, and the gap between the
/// two is itself the finding this item's brief asked to be reported rather than papered over.
///
/// <para><b>Why there is no `ExemptListed`/`UnaccountedKeys` here.</b> `ago-chat`'s equivalent can
/// split "not RBAC-gated" into "deliberately not, with a stated reason" (`TenantScopeExemptions`) and
/// "not accounted for at all" only because `17-01` built that catalogue and `Ago.Chat.Architecture.Tests.TenantScopeTests`
/// has enforced it at build time ever since. This product has never had either: no
/// `TenantScopeExemptions`-equivalent file exists anywhere in `ago-calendar`, and no architecture test
/// walks its handlers' IL for this property today. Inventing a reasoned exemption catalogue for this
/// product's ~49 handlers is exactly the kind of judgment call - reading each handler and writing why
/// it is safe - that `22-19`'s own README says a script (or a runtime reflector standing in for one)
/// must never make for `ago-chat`; the same restraint applies here, more so, since nobody has made that
/// judgment for this product even once. <see cref="NotGatedKeys"/> is offered instead: every entry
/// point that is not RBAC-gated, unclassified, for a human to read the same way `TenantScopeExemptions`'
/// own author once did for the first twenty-nine of `ago-chat`'s.</para>
///
/// <para><b>What this means for the console.</b> This product's <see cref="NotGatedKeys"/> must never
/// be rendered as if it carried the same weight as `ago-chat`'s `UnaccountedKeys` - a long list here is
/// expected (worker/consumer-side handlers, the unauthenticated public-booking surface, and genuinely
/// unreviewed gaps are all mixed together, indistinguishably, in this one list) rather than a red flag
/// the way even one entry in `ago-chat`'s own `UnaccountedKeys` is. See `docs/architecture/tenant-isolation.md`'s
/// own updated framing and the console's own rendering for how that distinction is kept visible rather
/// than lost the moment both numbers sit in the same table.</para>
/// </summary>
/// <param name="EntryPoints">Every public method of every `*Handler` class in
/// `Ago.Calendar.Application.UseCases`.</param>
/// <param name="HandlerClasses">How many distinct handler classes those entry points come from.</param>
/// <param name="RbacGated">Entry points that take a `TenantId` and call `IPermissionChecker`.</param>
/// <param name="NotGated">`EntryPoints - RbacGated` - not split further; see this record's own remarks
/// for why.</param>
/// <param name="NotGatedKeys">The entry points themselves, for a human to read - this product's
/// unclassified analogue of `ago-chat`'s `UnaccountedKeys`, carrying strictly less information (no
/// cross-reference against a reasoned catalogue exists to narrow it).</param>
public sealed record TenantScopeSnapshot(
    int EntryPoints,
    int HandlerClasses,
    int RbacGated,
    int NotGated,
    IReadOnlyList<string> NotGatedKeys);
