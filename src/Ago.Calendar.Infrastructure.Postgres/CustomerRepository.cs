using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ago.Calendar.Infrastructure.Postgres;

public sealed class CustomerRepository(AgoCalendarDbContext db) : ICustomerRepository
{
    public Task<Customer?> FindByPhoneAsync(
        TenantId tenantId, PhoneNumber phone, CancellationToken cancellationToken) =>
        // Both halves of the unique index, in the order it declares them - a lookup by phone alone
        // would be a cross-tenant read wearing ordinary clothes.
        db.Customers.FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Phone == phone, cancellationToken);

    public Task<Customer?> GetByIdAsync(CustomerId id, CancellationToken cancellationToken) =>
        db.Customers.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

    public async Task AddAsync(Customer customer, CancellationToken cancellationToken)
    {
        db.Customers.Add(customer);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SaveAsync(Customer customer, CancellationToken cancellationToken)
    {
        if (db.Entry(customer).State == EntityState.Detached)
        {
            db.Customers.Add(customer);
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
