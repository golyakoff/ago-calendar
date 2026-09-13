using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres;

namespace Ago.Calendar.Integration.Tests;

/// <summary>
/// Diagnostic, 2026-09-13: does <see cref="BookingSurfaceReadStore.ListOpenSlotsAsync"/> report the
/// full run's own end (start + service duration) for a service that needs more than one grid slot, or
/// only the first slot's own end? Reported by the author against the live golyakov.net tenant: a
/// 60-minute service on a 30-minute grid: the reservation itself (`ConsecutiveRunFinder`/
/// `BookingStore.ClaimSlotSql`) correctly blocks the full hour, but the widget's own button label is
/// built from this store's `EndsAt`, and a mismatch here is exactly what would make a 60-minute
/// service look like a 30-minute one to the customer before they ever pick it.
/// </summary>
[Collection(PostgresCollection.Name)]
public class OpenSlotsEndAtTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Monday = new(2026, 5, 4, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AServiceNeedingTwoSlots_ReportsTheFullRunsEnd_NotTheFirstSlotsOwnEnd()
    {
        var clock = new FixedClock(Monday);
        var seed = await CalendarSeed.WriteAsync(fixture);
        await CalendarSeed.AddWorkingHoursAsync(
            fixture, seed, new TimeOnly(9, 0), new TimeOnly(17, 0), CalendarSeed.EveryDay);
        // 30-minute slots, no buffer - the exact shape golyakov.net's live worker_schedules row has.
        await CalendarSeed.AddWeeklyScheduleAsync(fixture, seed, horizonDays: 7, slotMinutes: 30, bufferMinutes: 0);

        // A 60-minute service on that grid needs ceil(60/30) = 2 consecutive slots.
        var longService = Service.Create(
            new ServiceId(CalendarSeed.NewId()), seed.Tenant.Id, "Consultation", TimeSpan.FromMinutes(60), null);

        await using (var db = fixture.CreateDbContext())
        {
            db.Services.Add(longService);
            var worker = await new WorkerRepository(db).GetByIdAsync(seed.Worker.Id, CancellationToken.None);
            worker!.Offer(longService);
            await db.SaveChangesAsync();
        }

        var harness = new AvailabilityHarness(fixture, clock);
        await harness.MaterializeAsync(seed.Calendar.Id);

        var store = new BookingSurfaceReadStore(fixture.DataSource);
        var slots = await store.ListOpenSlotsAsync(
            seed.Calendar.Id, longService.Id, null, Monday, limit: 5, CancellationToken.None);

        Assert.NotEmpty(slots);
        var first = slots[0];

        // The claim: EndsAt must reflect the service's own 60-minute duration, not the first
        // 30-minute grid cell's own end.
        Assert.Equal(first.StartsAt.AddMinutes(60), first.EndsAt);
    }
}
