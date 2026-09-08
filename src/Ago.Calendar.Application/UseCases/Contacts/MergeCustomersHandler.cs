using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.Contacts;

/// <summary>
/// `23-60`/`adr/0147`: the other half of that ADR's own asymmetry argument - a phone match is a hint
/// a human now acts on, deliberately, rather than proof the platform acts on by itself.
///
/// <para><b>Gated on <see cref="Permission.CustomerEdit"/>, not <see cref="Permission.CustomerRead"/>.</b>
/// A merge tombstones one lead card and rewrites another's history - <see cref="RevealCustomerPhoneHandler"/>'s
/// own <c>CustomerRead</c> gate is for looking at a card, and this is the same "edit" class
/// <c>Permission.CustomerEdit</c> already names for <see cref="Customer.Describe"/>, just a more
/// consequential instance of it.</para>
///
/// <para><b>Cross-tenant merging is structurally impossible, not a runtime check that could be
/// bypassed.</b> Both ids are resolved through <see cref="ICustomerRepository.GetByIdAsync"/> and
/// compared against <see cref="MergeCustomers.TenantId"/> exactly the way
/// <see cref="RevealCustomerPhoneHandler"/> already does for one id - "wrong tenant reads like no such
/// customer", never a different, more informative error that would confirm a foreign id is real. The
/// write itself repeats the scoping: <see cref="ICustomerMergeStore"/>'s own remarks explain why its
/// bulk reassignment is scoped by tenant in its own SQL, not only by the ids this handler already
/// checked - two independent places a caller would have to get past, not one <c>if</c>.</para>
///
/// <para><b>The handler decides which side survives; the operator only decides whether to merge at
/// all.</b> The alternative - a request naming its own "survivor" id - was rejected: this product's
/// only path by which a *future* booking attaches to an existing customer is <c>BookingStore</c>'s own
/// upsert on <c>(tenant_id, phone) WHERE source = 'Booking'</c>, and only a
/// <see cref="CustomerSource.Booking"/>-sourced row can ever satisfy that partial unique index. If an
/// operator were free to tombstone the Booking-sourced row in favour of a Chat-sourced one, the very
/// next booking that customer makes would upsert a *third* row rather than reaching the console's own
/// chosen survivor - reintroducing, from inside the tool built to fix duplicates, the exact
/// "invisible" failure `adr/0147` warns a wrong merge already is. So: whichever candidate is
/// <see cref="CustomerSource.Booking"/>-sourced always survives when the other is
/// <see cref="CustomerSource.Chat"/>-sourced (the only asymmetric case the unique index can ever
/// produce - two <c>Booking</c>-sourced rows can never share a phone in the first place). Between two
/// <see cref="CustomerSource.Chat"/>-sourced rows, where no such asymmetry exists, the one first seen
/// earlier survives, tie-broken by id for determinism. The operator's own judgement - "are these
/// really the same person" - is exactly what the confirmation screen asks for, seeing both bookings
/// lists first; which database row keeps the id is an implementation detail the operator has no
/// reliable way to judge correctly and a wrong guess would actively break, so this handler does not
/// ask.</para>
/// </summary>
public sealed class MergeCustomersHandler(
    ICustomerRepository customers,
    ICustomerMergeStore merges,
    IPermissionChecker permissions,
    IIdGenerator idGenerator,
    IClock clock)
{
    public async Task<Result<CustomerMergeOutcome>> HandleAsync(
        MergeCustomers command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.OperatorId, command.TenantId, Permission.CustomerEdit, cancellationToken);
        if (!allowed)
        {
            return ContactsErrors.Forbidden(Permission.CustomerEdit);
        }

        if (command.FirstCustomerId == command.SecondCustomerId)
        {
            return ContactsErrors.CannotMergeCustomerWithItself(command.FirstCustomerId);
        }

        var first = await customers.GetByIdAsync(command.FirstCustomerId, cancellationToken);
        if (first is null || first.TenantId != command.TenantId)
        {
            return ContactsErrors.CustomerNotFound(command.FirstCustomerId);
        }

        var second = await customers.GetByIdAsync(command.SecondCustomerId, cancellationToken);
        if (second is null || second.TenantId != command.TenantId)
        {
            return ContactsErrors.CustomerNotFound(command.SecondCustomerId);
        }

        if (first.MergedIntoCustomerId is not null)
        {
            return ContactsErrors.CustomerAlreadyMerged(first.Id, first.MergedIntoCustomerId.Value);
        }

        if (second.MergedIntoCustomerId is not null)
        {
            return ContactsErrors.CustomerAlreadyMerged(second.Id, second.MergedIntoCustomerId.Value);
        }

        var (survivor, absorbed) = CustomerMergeSurvivorRule.Choose(first, second);

        var now = clock.UtcNow;
        survivor.AbsorbHistoryFrom(absorbed, now);
        absorbed.MarkMergedInto(survivor.Id, now);

        var mergeId = idGenerator.NewId(now);
        var result = await merges.MergeAsync(
            command.TenantId, survivor, absorbed, command.OperatorId, mergeId, now, cancellationToken);

        return new CustomerMergeOutcome(survivor.Id, absorbed.Id, result.BookingsMoved);
    }
}
