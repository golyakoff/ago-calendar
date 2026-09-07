using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Abstractions;

/// <summary>
/// `22-30`: the "erasure procedure across every table in this product" <see cref="ITenantRepository"/>'s
/// own remarks said would look nothing like ordinary CRUD, and would not belong on that port. It is a
/// separate interface rather than a third method on <see cref="ITenantRepository"/> for exactly that
/// reason: every other method there is shaped by a single-aggregate use case (`20-02`..`20-06`), while
/// this one is a whole-tenant deletion whose only real behaviour lives in the schema's own foreign
/// keys - the same "a bounded, purpose-built port rather than a CRUD grab-bag" call `ITenantRepository`'s
/// own doc comment already makes about itself.
///
/// <para><b>`adr/0149` rule 2: "a lifecycle operation completes when the module proves it, never when
/// chat has sent it."</b> <see cref="EraseAsync"/> does not report success because a `DELETE`
/// statement ran without throwing - it re-reads the database afterward and returns what that read
/// found. <see cref="TenantErasureResult.Confirmed"/> is that second, independent read, which is the
/// whole point: the calendar's own answer to "did it work" has to be a fact about its own Postgres,
/// not a completed statement AGO Chat is asked to trust.</para>
///
/// <para><b>Idempotent by construction</b> (`CLAUDE.md` rule 5 - at-least-once delivery is assumed
/// everywhere, and this port is reached over HTTP with no delivery guarantee stronger than that). A
/// tenant that no longer exists is not an error: <see cref="TenantErasureResult.TenantExisted"/> is
/// <see langword="false"/>, <see cref="TenantErasureResult.Confirmed"/> is <see langword="true"/> (there
/// is nothing left, which was already true before this call), and a caller cannot tell "just erased"
/// apart from "erased on a previous attempt" without comparing the two fields - which is exactly the
/// point: erasure is destructive and irreversible, and a second call must land on the identical answer
/// rather than throw over a row that is already gone.</para>
///
/// <para><b>No object storage to reach.</b> `personal-data.md` predicted this - "erasure there is
/// *easier*, because a person is a row rather than a substring of free text." Every table this product
/// holds for a tenant (`customers`, `events`, `workers`, `operators`, `pending_phone_verifications`,
/// `chat_module_registrations` and the rest of `Stage20CreateCalendarSchema`'s own foreign keys) cascades
/// from <c>tenants</c> already - this port issues exactly one delete and lets the schema do the rest,
/// unlike `Ago.Chat.Worker.SiteErasureJob`'s own bounded-batch, multi-step shape, which exists there
/// because chat also holds object storage and unbounded message history neither of which this product
/// has.</para>
/// </summary>
public interface ITenantErasureRepository
{
    Task<TenantErasureResult> EraseAsync(TenantId tenantId, CancellationToken cancellationToken);
}

/// <param name="TenantExisted">Whether a <see cref="Tenant"/> row was actually found and deleted by
/// this call - informational only (useful in a log line), never what a caller should branch on: see
/// this interface's own remarks on why <see cref="Confirmed"/> is the fact that matters.</param>
/// <param name="Confirmed">A fresh read of this database, taken after the delete, found no tenant row
/// for this id. Always <see langword="true"/> once this method returns without throwing - the type
/// still carries it explicitly, rather than the method returning nothing on success, because the wire
/// response this becomes (<c>TenantErasureResponse</c>) is what `adr/0149` rule 2 calls "the module's
/// own proof", and a caller reading a wire contract should see that proof named, not merely infer it
/// from an HTTP 200.</param>
public readonly record struct TenantErasureResult(bool TenantExisted, bool Confirmed);
