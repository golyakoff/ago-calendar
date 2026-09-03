using Ago.Calendar.Infrastructure.Postgres;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace Ago.Calendar.Integration.Tests;

/// <summary>
/// `20-24`: proves <see cref="PostgresHealthCheck"/> actually opens a connection rather than reporting
/// healthy unconditionally - the concern the item names directly ("a health endpoint that reports
/// something other than the truth is worse than none"). Against a real Postgres for the healthy case
/// (<see cref="PostgresFixture"/>, the same container this suite already pays for) and a genuinely
/// unreachable one for the unhealthy case - a loopback port nothing listens on, which fails fast
/// rather than waiting out a real timeout budget.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresHealthCheckTests(PostgresFixture fixture)
{
    [Fact]
    public async Task CheckHealthAsync_WithAReachableDatabase_ReportsHealthy()
    {
        var check = new PostgresHealthCheck(fixture.DataSource);

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task CheckHealthAsync_WithAnUnreachableDatabase_ReportsUnhealthy()
    {
        // Port 1 on loopback: nothing binds to it, so the connection attempt is refused immediately -
        // no wait budget to spend, unlike a real host that is merely slow to answer.
        await using var dataSource = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=1;Database=nope;Username=nope;Password=nope;Timeout=2");
        var check = new PostgresHealthCheck(dataSource);

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }
}
