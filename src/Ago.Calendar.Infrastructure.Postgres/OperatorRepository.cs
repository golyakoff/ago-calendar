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

    /// <summary>See the port's own remarks: zero or more-than-one candidates both come back as
    /// <see langword="null"/>, so a collision is refused rather than guessed through.</summary>
    public async Task<Operator?> FindInvitedByEmailAsync(InvitedEmail email, CancellationToken cancellationToken)
    {
        var candidates = await db.Operators
            .Include("_roles")
            .Where(o => o.ExternalSubjectId == null && o.InvitedEmail == email)
            .ToListAsync(cancellationToken);

        return candidates.Count == 1 ? candidates[0] : null;
    }

    public async Task<IReadOnlyList<Operator>> ListForTenantAsync(TenantId tenantId, CancellationToken cancellationToken) =>
        await db.Operators
            .Include("_roles")
            .Where(o => o.TenantId == tenantId)
            .OrderBy(o => o.DisplayName)
            .ToListAsync(cancellationToken);

    public async Task AddAsync(Operator @operator, CancellationToken cancellationToken)
    {
        db.Operators.Add(@operator);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// `20-12`'s first caller of a write path that only ever mutates an already-loaded
    /// <see cref="Operator"/> - <c>Operator.Grant</c>/<c>Operator.Revoke</c> add or remove a
    /// <see cref="RoleAssignment"/> in place. The real path is a <c>GetByIdAsync</c> earlier in the
    /// same request, so the instance is already tracked by this scoped <see cref="AgoCalendarDbContext"/>
    /// and its change tracker sees the collection edit on its own - which is exactly why this does
    /// <b>not</b> call <c>db.Operators.Update(@operator)</c> the way <c>BookingCalendarRepository.SaveAsync</c>
    /// does. <c>Update()</c> walks the whole navigation graph and marks every entity it finds -
    /// including each <see cref="RoleAssignment"/> - as <c>Modified</c> rather than <c>Added</c>,
    /// because their composite key is client-assigned and EF cannot tell "new" from "existing" by key
    /// alone; that would silently turn a freshly granted role into a no-op <c>UPDATE</c> of a row that
    /// does not exist yet. The <c>Detached</c> branch below is only a defensive fallback for a caller
    /// that reconstructed the aggregate outside this context (<c>EventRepository.SaveAsync</c>'s own
    /// precedent) - it is not the path role grants/revokes actually take.
    /// </summary>
    public async Task SaveAsync(Operator @operator, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(@operator);

        if (db.Entry(@operator).State == EntityState.Detached)
        {
            db.Operators.Attach(@operator);
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
