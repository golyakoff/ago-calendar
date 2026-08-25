using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ago.Calendar.Infrastructure.Postgres;

public sealed class OperatorRepository(AgoCalendarDbContext db) : IOperatorRepository
{
    public Task<Operator?> GetByIdAsync(OperatorId id, CancellationToken cancellationToken) =>
        db.Operators
            .Include("_roles")
            .FirstOrDefaultAsync(o => o.Id == id, cancellationToken);

    public Task<Operator?> FindByExternalSubjectIdAsync(string externalSubjectId, CancellationToken cancellationToken) =>
        db.Operators
            .Include("_roles")
            .FirstOrDefaultAsync(o => o.ExternalSubjectId == externalSubjectId, cancellationToken);

    public async Task AddAsync(Operator @operator, CancellationToken cancellationToken)
    {
        db.Operators.Add(@operator);
        await db.SaveChangesAsync(cancellationToken);
    }
}
