using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace Ago.Calendar.Infrastructure.Postgres;

/// <summary>
/// `20-24`: readiness should mean "can do the job," not "the process is running" - ported from
/// <c>Ago.Chat.Infrastructure.Postgres.PostgresHealthCheck</c> (`2-04`) unchanged in shape.
///
/// Lives here rather than in <c>Ago.Calendar.Module</c> or <c>Ago.Calendar.Api</c> directly: this
/// product's own <c>PersistenceBoundaryTests</c>-equivalent architecture rule (adr/0004, "one project
/// per external technology") makes <c>Ago.Calendar.Infrastructure.Postgres</c> the one place allowed
/// to see <c>Npgsql</c> at all, a health check included - the same reasoning that already moved this
/// class in <c>ago-chat</c> out of its host and into its own Infrastructure project. The alternative,
/// a health check class in <c>Ago.Calendar.Api</c> that opens an <see cref="NpgsqlConnection"/>
/// itself, would put an external-technology dependency in a host project the arch tests do not expect
/// to carry one - IHealthCheck is the ASP.NET Core port already; nothing here needs a port of its own.
/// </summary>
public sealed class PostgresHealthCheck(NpgsqlDataSource dataSource) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand("SELECT 1", connection);
            await command.ExecuteScalarAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Cannot reach Postgres.", ex);
        }
    }
}
