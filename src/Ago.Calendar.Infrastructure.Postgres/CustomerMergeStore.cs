using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ago.Calendar.Infrastructure.Postgres;

/// <summary>
/// `23-60`/`adr/0147`: one transaction, three effects - <see cref="ICustomerMergeStore"/>'s own
/// remarks explain why none of the three may commit without the other two.
/// <see cref="TenantErasureRepository"/> is the closest precedent in this codebase for mixing a
/// tracked-entity save with a bulk statement inside one explicit transaction; this reuses that shape
/// rather than inventing a second one.
/// </summary>
public sealed class CustomerMergeStore(AgoCalendarDbContext db) : ICustomerMergeStore
{
    /// <summary>
    /// `23-60`: no foreign key on <c>operator_id</c> - the identical reason
    /// <c>contact_phone_reveals.operator_id</c> has none (`22-05`/`adr/0093`: this product keeps no
    /// local <c>operators</c> table to reference any more). Unlike that table, <c>tenant_id</c>,
    /// <c>survivor_customer_id</c> and <c>absorbed_customer_id</c> all do carry a foreign key - see the
    /// migration's own remarks for why this row is a tenant-internal administration log
    /// (`role_change_records`'s own shape) rather than evidence that must outlive the tenant it names
    /// (`contact_phone_reveals`'s own shape).
    /// </summary>
    private const string InsertMergeRecordSql =
        """
        insert into customer_merges
            (id, tenant_id, survivor_customer_id, absorbed_customer_id, operator_id, bookings_moved, merged_at)
        values ({0}, {1}, {2}, {3}, {4}, {5}, {6})
        """;

    public async Task<CustomerMergeResult> MergeAsync(
        TenantId tenantId,
        Customer survivor,
        Customer absorbed,
        OperatorId operatorId,
        Guid mergeId,
        DateTimeOffset mergedAt,
        CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // `ICustomerMergeStore`'s own remarks: scoped by tenant in this WHERE clause too, on top of
        // the absorbed customer's own id - a caller cannot express a cross-tenant reassignment through
        // this method even if the ids it was handed somehow crossed a tenant boundary upstream.
        var bookingsMoved = await db.Events
            .Where(e => e.TenantId == tenantId && e.CustomerId == absorbed.Id)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(e => e.CustomerId, (CustomerId?)survivor.Id), cancellationToken);

        // Both aggregates already carry the mutation MergeCustomersHandler applied
        // (Customer.AbsorbHistoryFrom / Customer.MarkMergedInto) - this persists that decision, the
        // same defensive attach-if-detached shape CustomerRepository.SaveAsync already uses, so this
        // store behaves correctly whether or not the caller's DbContext scope already tracks these
        // two entities.
        Attach(survivor);
        Attach(absorbed);
        await db.SaveChangesAsync(cancellationToken);

        await db.Database.ExecuteSqlRawAsync(
            InsertMergeRecordSql,
            [mergeId, tenantId.Value, survivor.Id.Value, absorbed.Id.Value, operatorId.Value, bookingsMoved, mergedAt],
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return new CustomerMergeResult(bookingsMoved);
    }

    private void Attach(Customer customer)
    {
        // Already tracked (the ordinary path: the same DbContext scope loaded this entity through
        // ICustomerRepository.GetByIdAsync before MergeCustomersHandler mutated it) - EF's change
        // tracker already saw every property the domain methods touched, snapshot-tracking working
        // regardless of the setters' own accessibility, so there is nothing to do.
        //
        // Detached - `db.Update`, not `db.Attach`: `Attach` marks every property `Unchanged`, which
        // would persist nothing, while `Update` marks the whole entity `Modified`, a full-row write
        // that is correct here because this method has no prior snapshot to diff a detached instance
        // against - the identical reasoning `CustomerRepository.SaveAsync`'s own `Add`-if-detached
        // branch states for the same situation on the ordinary single-customer save path.
        if (db.Entry(customer).State == EntityState.Detached)
        {
            db.Update(customer);
        }
    }
}
