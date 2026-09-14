namespace Ago.Calendar.Application.Abstractions;

/// <summary>
/// `24-17`: this product's own answer to the same question `Ago.Chat.Application.Abstractions.ITenantScopeInspector`
/// answers for `ago-chat` - "how many of this product's own use cases are RBAC-gated, right now, in
/// the assembly that is actually running" - read at runtime from `Ago.Calendar.Application.dll` with
/// Mono.Cecil, the same technology and the same reasoning that port's own remarks give for why this is
/// a port at all rather than a static helper `Ago.Calendar.Api` calls directly.
///
/// <para><b>Deliberately not a shared contract between the two products.</b> `adr/0027` is explicit
/// that this product copies `ago-chat`'s own mechanisms rather than sharing them across the repository
/// boundary (`AuthenticationSetup.cs`'s own remarks: "adr/0022's token flow, this product's own copy").
/// The same reasoning applies here, doubly: not only would a shared `Ago.Platform.*` package for this
/// be premature generalisation for a mechanism with exactly two callers, but the *shape* of the answer
/// genuinely differs between the two products (see <see cref="TenantScopeSnapshot"/>'s own remarks) -
/// a shared interface would either have to carry fields one product cannot fill honestly, or be
/// narrowed to the intersection and lose the distinction that matters most in `ago-chat`'s own
/// case.</para>
/// </summary>
public interface ITenantScopeInspector
{
    Task<TenantScopeSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);
}
