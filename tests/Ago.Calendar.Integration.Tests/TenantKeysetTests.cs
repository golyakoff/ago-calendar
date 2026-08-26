using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres;

namespace Ago.Calendar.Integration.Tests;

/// <summary>
/// `20-02` added <see cref="ITenantRepository.ListIdsAsync"/> so that the materialisation job can
/// visit every tenant while keeping every *calendar* read tenant-scoped. It is hand-written SQL - the
/// strongly-typed id has no <c>&gt;</c> operator for EF to translate - so it gets a test against a
/// real Postgres rather than a review.
/// </summary>
[Collection(PostgresCollection.Name)]
public class TenantKeysetTests(PostgresFixture fixture)
{
    [Fact]
    public async Task PagingWithAKeysetVisitsEveryTenantExactlyOnce()
    {
        // This container is shared, so other tests' tenants are in the table too - which is the
        // honest shape of the query the job runs, and makes "walked everything, in order, no
        // repeats" the only assertion that means anything here.
        var mine = new List<TenantId>();
        for (var i = 0; i < 3; i++)
        {
            mine.Add((await CalendarSeed.WriteAsync(fixture)).Tenant.Id);
        }

        await using var db = fixture.CreateDbContext();
        var repository = new TenantRepository(db);

        var walked = new List<TenantId>();
        TenantId? after = null;
        while (true)
        {
            var page = await repository.ListIdsAsync(after, limit: 2, CancellationToken.None);
            if (page.Count == 0)
            {
                break;
            }

            walked.AddRange(page);
            after = page[^1];
        }

        Assert.Equal(walked.Count, walked.Distinct().Count());
        Assert.Equal(walked.OrderBy(id => id.Value), walked);
        Assert.All(mine, id => Assert.Contains(id, walked));
    }

    [Fact]
    public async Task APageIsBoundedByItsLimit()
    {
        await CalendarSeed.WriteAsync(fixture);
        await CalendarSeed.WriteAsync(fixture);

        await using var db = fixture.CreateDbContext();
        var page = await new TenantRepository(db).ListIdsAsync(after: null, limit: 1, CancellationToken.None);

        Assert.Single(page);
    }

    [Fact]
    public async Task AKeysetCursorPastTheLastRowReturnsNothing()
    {
        await CalendarSeed.WriteAsync(fixture);

        await using var db = fixture.CreateDbContext();
        var repository = new TenantRepository(db);

        // UUID v7 is time-ordered, so a value generated now sorts after every row already written -
        // which is the terminating condition the job's own loop relies on.
        var beyondTheEnd = new TenantId(Guid.CreateVersion7(DateTimeOffset.UtcNow.AddYears(50)));

        Assert.Empty(await repository.ListIdsAsync(beyondTheEnd, limit: 10, CancellationToken.None));
    }

    [Fact]
    public async Task AZeroOrNegativeLimitIsRejected()
    {
        await using var db = fixture.CreateDbContext();
        var repository = new TenantRepository(db);

        // A limit of zero would return an empty page and stop the job's walk on its first iteration,
        // silently materialising nothing at all - a misconfiguration worth failing loudly on.
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => repository.ListIdsAsync(after: null, limit: 0, CancellationToken.None));
    }
}
