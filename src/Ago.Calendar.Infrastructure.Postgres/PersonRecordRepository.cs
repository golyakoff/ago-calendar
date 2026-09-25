using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ago.Calendar.Infrastructure.Postgres;

/// <summary>`adr/0184`: the operator-side load-mutate-save adapter for <see cref="PersonRecord"/> -
/// see <see cref="IPersonRecordRepository"/> for why the booking path's own upsert is not here.</summary>
public sealed class PersonRecordRepository(AgoCalendarDbContext db) : IPersonRecordRepository
{
    public Task<PersonRecord?> GetByIdAsync(Guid personId, CancellationToken cancellationToken) =>
        db.PersonRecords.FirstOrDefaultAsync(p => p.PersonId == personId, cancellationToken);

    // Both halves of ix_person_records_tenant_phone, in the order it declares them - a lookup by phone
    // alone would be a cross-tenant read wearing ordinary clothes. MinAsync over a nullable projection
    // returns null for an empty set rather than throwing, which is exactly the "never verified here"
    // answer the resolver wants.
    public Task<DateTimeOffset?> FindPhoneVerifiedAtAsync(
        TenantId tenantId, PhoneNumber phone, CancellationToken cancellationToken) =>
        db.PersonRecords
            .Where(p => p.TenantId == tenantId && p.Phone == phone && p.PhoneVerifiedAt != null)
            .MinAsync(p => p.PhoneVerifiedAt, cancellationToken);

    public async Task AddAsync(PersonRecord record, CancellationToken cancellationToken)
    {
        db.PersonRecords.Add(record);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SaveAsync(PersonRecord record, CancellationToken cancellationToken)
    {
        if (db.Entry(record).State == EntityState.Detached)
        {
            db.PersonRecords.Add(record);
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
