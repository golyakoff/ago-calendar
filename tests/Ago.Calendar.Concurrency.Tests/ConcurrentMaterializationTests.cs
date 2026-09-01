using Ago.Calendar.Application.UseCases.MaterializeAvailability;
using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres;
using Ago.Calendar.Infrastructure.Time;
using Ago.Platform.Kernel;
using Microsoft.EntityFrameworkCore;

namespace Ago.Calendar.Concurrency.Tests;

/// <summary>
/// Two <c>Ago.Calendar.Worker</c> replicas materialising the same day at the same instant.
///
/// <para><b>Real concurrency, not sequential awaits.</b> Each run gets its own
/// <c>DbContext</c> on its own connection and both are started before either is awaited, so the two
/// transactions are genuinely open at once - the same bar `4-03`'s
/// <c>WaitingConversationClaimQuery</c> was held to in AGO Chat. Awaiting one and then the other
/// would test that the second run skips a day the first has already committed, which is a different
/// and much weaker claim (and is already covered in
/// <c>Ago.Calendar.Integration.Tests.AvailabilityMaterializationTests</c>).</para>
///
/// <para><b>Why the handler and not <c>AvailabilityMaterializationJob</c> itself.</b> The job is a
/// <see cref="PeriodicTimer"/> loop around one call per calendar; the unit that races is the call.
/// Driving two jobs would add a timer, a host lifetime and a service provider to the test without
/// adding a second writer to the database.</para>
/// </summary>
[Collection(ConcurrencyCollection.Name)]
public class ConcurrentMaterializationTests(ConcurrencyFixture fixture)
{
    private static readonly DateTimeOffset Monday = new(2026, 5, 4, 0, 0, 0, TimeSpan.Zero);
    private static readonly SystemWallClockResolver WallClock = new();

    [Fact]
    public async Task TwoReplicasMaterializingTheSameWindow_ProduceNoDuplicateSlots()
    {
        var seed = await SeedAsync();

        // Started before either is awaited: both handlers are inside their own transaction while the
        // other is writing. Task.WhenAll on two already-running tasks, not two awaits in a row.
        var left = Task.Run(() => MaterializeAsync(seed.CalendarId));
        var right = Task.Run(() => MaterializeAsync(seed.CalendarId));

        var results = await Task.WhenAll(left, right);

        await using var db = fixture.CreateDbContext();
        var slots = await db.Events
            .Where(e => e.WorkerId == seed.WorkerId)
            .OrderBy(e => e.StartsAt)
            .ToListAsync();

        // One full week of slots exists exactly once. Not "no exception was thrown": the assertion
        // is on the rows, because a duplicate slot is two customers in one chair and it would be
        // invisible to a test that only checked for errors.
        Assert.NotEmpty(slots);
        Assert.Equal(slots.Count, slots.Select(s => s.StartsAt).Distinct().Count());

        // And between the two runs, exactly one set of rows was written - ON CONFLICT DO NOTHING
        // dropped the loser's rows rather than aborting its transaction, so neither run failed and
        // neither run had to be retried.
        Assert.Equal(slots.Count, results.Sum(result => result.SlotsInserted));
    }

    [Fact]
    public async Task FourReplicasMaterializingTheSameWindow_StillProduceNoDuplicateSlots()
    {
        // Two writers can pass a race by luck; four in a tighter window is the same mechanism under
        // more pressure. Still no coordination anywhere in the job - the constraint is the only
        // arbiter, and adding a lease or a leader election would only add a component that can fail.
        var seed = await SeedAsync();

        var runs = Enumerable.Range(0, 4)
            .Select(_ => Task.Run(() => MaterializeAsync(seed.CalendarId)))
            .ToList();

        var results = await Task.WhenAll(runs);

        await using var db = fixture.CreateDbContext();
        var slots = await db.Events.Where(e => e.WorkerId == seed.WorkerId).ToListAsync();

        Assert.NotEmpty(slots);
        Assert.Equal(slots.Count, slots.Select(s => s.StartsAt).Distinct().Count());
        Assert.Equal(slots.Count, results.Sum(result => result.SlotsInserted));
    }

    [Fact]
    public async Task TwoWritersInsertingTheIdenticalBatchAtTheSameInstant_LandItExactlyOnce()
    {
        // The end-to-end tests above could in principle pass without ever racing: if one replica
        // committed before the other read the day set, the second would skip every day and insert
        // nothing, and every assertion would still hold. This one removes that escape by taking the
        // day-set check out of the picture entirely - both writers are handed the *same* generated
        // rows and released together, so the only thing that can stop a duplicate is the statement
        // itself.
        var seed = await SeedAsync();
        var left = GenerateOneDay(seed);
        var right = GenerateOneDay(seed);

        // Different ids, identical instants - which is exactly what two replicas produce, since each
        // generates its own UUIDs. A primary-key conflict would therefore prove nothing here; the
        // constraint that has to do the work is the GiST exclusion one.
        Assert.Equal(
            left.Select(e => e.StartsAt),
            right.Select(e => e.StartsAt));
        Assert.Empty(left.Select(e => e.Id).Intersect(right.Select(e => e.Id)));

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<int> InsertAsync(List<Event> batch)
        {
            await using var db = fixture.CreateDbContext();
            var repository = new EventRepository(db);
            await gate.Task;
            return await repository.InsertAvailableSlotsAsync(batch, CancellationToken.None);
        }

        var first = InsertAsync(left);
        var second = InsertAsync(right);
        gate.SetResult();

        // Neither throws. ON CONFLICT DO NOTHING turns the loser's rows into no-ops instead of
        // aborting its transaction, which is why the job needs no retry, no lease and no leader
        // election - the whole coordination story is this one clause.
        var inserted = await Task.WhenAll(first, second);

        Assert.Equal(left.Count, inserted.Sum());
        Assert.Contains(inserted, count => count == 0);

        await using var db = fixture.CreateDbContext();
        var stored = await db.Events.Where(e => e.WorkerId == seed.WorkerId).ToListAsync();
        Assert.Equal(left.Count, stored.Count);
        Assert.Equal(stored.Count, stored.Select(e => e.StartsAt).Distinct().Count());
    }

    /// <summary>One worker's Monday, built the way the materialiser builds it - fresh ids, identical
    /// instants.</summary>
    private static List<Event> GenerateOneDay(SeededCalendar seed)
    {
        var window = WallClock.ToInstantWindow(
            new CalendarTimeZone("Europe/Moscow"), new DateOnly(2026, 5, 4), new TimeOnly(9, 0), new TimeOnly(18, 0))!.Value;

        return SlotGrid.Fill(window, TimeSpan.FromMinutes(45), TimeSpan.FromMinutes(10))
            .Select(slot => Event.Materialize(
                new EventId(NewId()), seed.TenantId, seed.CalendarId, seed.WorkerId,
                slot, new DateOnly(2026, 5, 4), Monday))
            .ToList();
    }

    private async Task<AvailabilityMaterialized> MaterializeAsync(CalendarId calendarId)
    {
        await using var db = fixture.CreateDbContext();
        var handler = new MaterializeAvailabilityHandler(
            new BookingCalendarRepository(db),
            new WorkerRepository(db),
            new WorkingHoursRuleRepository(db),
            new ServiceRepository(db),
            new EventRepository(db),
            WallClock,
            new UuidV7Generator(),
            new FixedClock(Monday));

        return await handler.HandleAsync(new MaterializeAvailability(calendarId, 6), CancellationToken.None);
    }

    private async Task<SeededCalendar> SeedAsync()
    {
        var tenant = Tenant.Register(new TenantId(NewId()), "Barbershop", new TenantPublicKey("shop-" + NewId().ToString("N")), Monday);
        var calendar = BookingCalendar.Create(
            new CalendarId(NewId()), tenant.Id, "Main", new CalendarTimeZone("Europe/Moscow"), 10, Monday);
        var worker = Worker.Create(new WorkerId(NewId()), tenant.Id, "Doe", "Alex", null, Monday);
        var service = Service.Create(new ServiceId(NewId()), tenant.Id, "Haircut", TimeSpan.FromMinutes(45));

        calendar.Publish();
        worker.JoinCalendar(calendar);
        worker.Offer(service);

        await using var db = fixture.CreateDbContext();
        db.Tenants.Add(tenant);
        db.Calendars.Add(calendar);
        db.Services.Add(service);
        db.Workers.Add(worker);

        foreach (var day in Enum.GetValues<DayOfWeek>())
        {
            db.WorkingHoursRules.Add(WorkingHoursRule.For(
                new WorkingHoursRuleId(NewId()), worker, calendar, day, new TimeOnly(9, 0), new TimeOnly(18, 0)));
        }

        await db.SaveChangesAsync();

        return new SeededCalendar(tenant.Id, calendar.Id, worker.Id);
    }

    private static Guid NewId() => Guid.CreateVersion7(DateTimeOffset.UtcNow);
}
