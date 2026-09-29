using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Application.Mapping;
using Ago.Calendar.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.ErasePerson;

/// <summary>
/// `26-275`/`adr/0189`: an Admin erases a client - the calendar-initiated half of full person
/// erasure. Author decision on issue 1815, Option A: full person erasure (chat's Person, its
/// conversations, its messages), **but no analytics recompute** - this handler's own job is only the
/// calendar's own slice of that: refuse while a live future booking exists, otherwise erase this
/// product's own person data and tell chat to cascade.
///
/// <para><b>Calendar-initiated, not chat-initiated - <c>adr/0189</c> inverts `adr/0184`'s own sketch.</b>
/// `adr/0184`'s Consequences describe "person erasure deletes one Person in chat and cascades the
/// calendar's operational record + events by id" - chat first, calendar second. That shape needs chat
/// to ask the calendar "does this person have a live future booking" before it dares erase, which is a
/// synchronous server-to-server read of a rule-8 fact CLAUDE.md forbids outright (rule 8: "a write
/// decision" - and refusing an erasure is one - "comes from the database inside the transaction,"
/// never a remote call). The only way to keep the guard rule-8-legal is to run it where the fact
/// already lives: here. So this handler is the one that decides "may this person be erased at all,"
/// and <c>PersonErased</c> runs the opposite direction from `adr/0184`'s own sketch - calendar tells
/// chat, not chat tells calendar.</para>
///
/// <para><b>Permission first, the identical ordering every other console write in this product
/// takes</b> (<c>EnterManualBookingHandler</c>'s own remarks): a caller with no right never learns
/// whether a person with this id exists in this tenant at all.</para>
///
/// <para><b>Existence is checked before the transaction, the guard inside it</b> - the identical split
/// <c>DeleteWorkerHandler</c>'s own remarks state: "does a person with this id exist in this tenant" is
/// a question about identity, not about a concurrent write to <c>events</c>, so there is no race for a
/// courtesy read to lose. Only "does this person have a live future booking" has to be inside the same
/// statement as the delete (<see cref="IPersonEraseStore"/>'s own remarks) - and when the guarded
/// attempt below comes back <see langword="false"/>, this handler re-reads
/// <see cref="IPersonRecordRepository"/> once more to tell "already gone" from "blocked by a future
/// booking" apart, the identical disambiguation <c>DeleteWorkerHandler</c> already performs for its
/// own guarded delete.</para>
/// </summary>
public sealed class ErasePersonHandler(
    IPersonRecordRepository personRecords,
    IPersonEraseStore store,
    IPermissionChecker permissions,
    IIdGenerator idGenerator,
    IClock clock)
{
    public async Task<Result> HandleAsync(ErasePerson command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.OperatorId, command.TenantId, Permission.CustomerErase, cancellationToken);
        if (!allowed)
        {
            return ErasePersonErrors.Forbidden(Permission.CustomerErase);
        }

        var person = await personRecords.GetByIdAsync(command.PersonId, cancellationToken);
        if (person is null || person.TenantId != command.TenantId)
        {
            // Wrong tenant reads like no such person - the identical cross-tenant info-hiding shape
            // ContactsErrors.CustomerNotFound and BookingLifecycleErrors.WrongTenant both already
            // apply: an operator of tenant A must not learn that a person id is real but belongs to
            // tenant B.
            return ErasePersonErrors.PersonNotFound(command.PersonId);
        }

        var now = clock.UtcNow;
        var personErased = PersonErasedMapper.ToEnvelope(command.PersonId, command.TenantId, now, idGenerator);

        var erased = await store.TryEraseAsync(
            new PersonEraseAttempt(command.TenantId, command.PersonId, now, personErased), cancellationToken);
        if (erased)
        {
            return Result.Success();
        }

        // Zero rows affected inside the guarded delete means one of two things: this person still has
        // a live future booking, or the row was already gone (a concurrent second erasure landed
        // first). This follow-up read only decides how to word the refusal - it changes nothing about
        // what was or was not erased, which the guarded statement already settled atomically - the
        // identical "a follow-up read cannot reopen a race the write already closed" reasoning
        // DeleteWorkerHandler's own remarks give for itself.
        var stillThere = await personRecords.GetByIdAsync(command.PersonId, cancellationToken);
        if (stillThere is null || stillThere.TenantId != command.TenantId)
        {
            return ErasePersonErrors.PersonNotFound(command.PersonId);
        }

        return ErasePersonErrors.FutureBookingsExist(command.PersonId);
    }
}
