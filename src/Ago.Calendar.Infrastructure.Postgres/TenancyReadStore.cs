using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Dapper;
using Npgsql;

namespace Ago.Calendar.Infrastructure.Postgres;

/// <summary>
/// adr/0004's read side, `22-14`'s own: Dapper over the shared <see cref="NpgsqlDataSource"/>, the
/// same call <see cref="ContactsReadStore"/> already argued - a read model never borrows the write
/// context's change tracker or its ambient transaction.
/// </summary>
public sealed class TenancyReadStore(NpgsqlDataSource dataSource) : ITenancyReadStore
{
    /// <summary>
    /// <b>A left join, not an inner one.</b> The two facts are written by different things at
    /// different times - <c>RoleAssignmentsChangedConsumer</c> replicates the grant, module
    /// provisioning writes the <c>tenants</c> row - so an inner join would silently drop a tenancy
    /// that resolves perfectly well and leave the switcher unable to name it (see
    /// <see cref="TenancyRow.TenantName"/>'s own remarks).
    ///
    /// <para><b>Rides the primary key.</b> <c>role_assignment_projections</c> is keyed
    /// <c>(operator_id, tenant_id)</c>, so filtering on the leading column is an index scan of a
    /// handful of rows - <c>RoleAssignmentProjectionConfiguration</c>'s own remarks already state why
    /// no separate index on <c>operator_id</c> exists. This item adds no index and no migration.</para>
    ///
    /// <para><b>Ordered in SQL, and by name first</b> - the order a human reads a switcher in, and
    /// `ago-chat`'s <c>ListMyTenanciesHandler</c> makes the same choice for the same list. The
    /// <c>tenant_id</c> tiebreak is what makes it a total order: without it, two tenants sharing a
    /// name (or two with none yet) would come back in whatever order the plan happened to produce,
    /// and a dropdown that reshuffles between loads is its own small bug.</para>
    /// </summary>
    private const string Sql =
        """
        select p.tenant_id as "TenantId", coalesce(t.name, '') as "TenantName"
        from role_assignment_projections p
        left join tenants t on t.id = p.tenant_id
        where p.operator_id = @OperatorId
        order by coalesce(t.name, ''), p.tenant_id
        """;

    public async Task<IReadOnlyList<TenancyRow>> ListForOperatorAsync(
        OperatorId operatorId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<TenancyQueryRow>(new CommandDefinition(
            Sql, new { OperatorId = operatorId.Value }, cancellationToken: cancellationToken));

        return [.. rows.Select(row => new TenancyRow(new TenantId(row.TenantId), row.TenantName))];
    }

    private sealed record TenancyQueryRow(Guid TenantId, string TenantName);
}
