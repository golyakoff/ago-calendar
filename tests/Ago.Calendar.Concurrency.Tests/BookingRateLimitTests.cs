using Ago.Calendar.Application.UseCases.BookEvent;
using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres;
using Ago.Platform.Caching.Redis;
using Ago.Platform.Kernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Polly;

namespace Ago.Calendar.Concurrency.Tests;

/// <summary>
/// The public booking endpoint's two buckets, against a real Redis running the platform's real Lua
/// token bucket - `3-05`'s <c>RateLimitingConcurrencyTests</c> is the precedent and the bar.
///
/// <para><b>Why these are correctness tests and not configuration smoke tests.</b> This endpoint is
/// unauthenticated and it writes rows: a lead card holding somebody's phone number, and a state
/// transition that takes a slot out of circulation for everybody else. The second is a
/// denial-of-service against a real shop's entire day, achievable with nothing but a list of event
/// ids. A limit nobody exercises is a limit nobody knows is wired.</para>
///
/// <para>The limiter is exercised through <see cref="BookEventHandler"/> rather than called
/// directly, because what has to hold is not "Redis counts" - the platform proves that - but
/// "the handler consults it, in the right order, before it writes anything".</para>
///
/// <para>Phone numbers are invented <c>+7999...</c> values belonging to nobody
/// (<c>personal-data.md</c>).</para>
/// </summary>
[Collection(ConcurrencyCollection.Name)]
public class BookingRateLimitTests(ConcurrencyFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 5, 4, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ABurstFromOnePhone_AllowsExactlyCapacityAndDeniesTheRestWithARetryAfter()
    {
        var seed = await SeedAsync();
        var slots = await AvailableSlotsAsync(seed, 12);

        // Capacity 3, refill slow enough that nothing meaningfully refills during the burst. The
        // calendar bucket is opened wide so this test measures the phone bucket alone.
        var options = new BookingRateLimitOptions
        {
            PerPhoneCapacity = 3,
            PerPhoneRefillPerSecond = 0.001,
            PerCalendarCapacity = 10_000,
            PerCalendarRefillPerSecond = 10_000,
        };

        var outcomes = new List<BookingOutcome>();
        foreach (var slot in slots)
        {
            outcomes.Add(await BookAsync(seed, slot.Id, "+79993000001", options));
        }

        Assert.Equal(3, outcomes.Count(outcome => outcome.IsSuccess));

        var denied = outcomes.Where(outcome => !outcome.IsSuccess).ToList();
        Assert.Equal(9, denied.Count);
        Assert.All(denied, outcome => Assert.Equal("booking.rate_limited", outcome.Error!.Value.Code));

        // The header's value, carried out of the handler as a TimeSpan rather than buried in prose.
        // api-design.md promises a 429 arrives with Retry-After and that clients honour it; a
        // retry-after only a human can read is one no client backs off on.
        Assert.All(denied, outcome => Assert.True(outcome.RetryAfter > TimeSpan.Zero));

        // And the denials cost the database nothing: every slot past the third is still Available,
        // so a caller over their budget never reached a write.
        await using var db = fixture.CreateDbContext();
        var untouched = await db.Events.CountAsync(
            e => e.CalendarId == seed.CalendarId && e.Status == EventStatus.Available);
        Assert.Equal(9, untouched);
    }

    [Fact]
    public async Task ConcurrentAttemptsOnOnePhonesBucket_NeverExceedCapacity()
    {
        // The bucket's own atomicity, under real simultaneity: sixteen callers, each on its own
        // connection, released together. A read-then-decrement in C# would let several of them read
        // "one token left" and all spend it - the exact race the platform's Lua script closes, and
        // the reason this test drives concurrent callers rather than a loop.
        var seed = await SeedAsync();
        var slots = await AvailableSlotsAsync(seed, 16);

        var options = new BookingRateLimitOptions
        {
            PerPhoneCapacity = 4,
            PerPhoneRefillPerSecond = 0.001,
            PerCalendarCapacity = 10_000,
            PerCalendarRefillPerSecond = 10_000,
        };

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = slots.Select(slot => Task.Run(async () =>
        {
            await gate.Task;
            return await BookAsync(seed, slot.Id, "+79993000002", options);
        })).ToList();

        await Task.Delay(50);
        gate.SetResult();
        var outcomes = await Task.WhenAll(attempts);

        Assert.Equal(4, outcomes.Count(outcome => outcome.IsSuccess));
    }

    [Fact]
    public async Task TheCalendarBucket_BoundsAFloodArrivingFromManyDifferentPhones()
    {
        // The bucket the per-phone one cannot see. Ten distinct numbers, each well inside its own
        // budget, all aimed at one calendar - which is what a real abuse attempt looks like, and why
        // one bucket would not have been enough.
        var seed = await SeedAsync();
        var slots = await AvailableSlotsAsync(seed, 10);

        var options = new BookingRateLimitOptions
        {
            PerPhoneCapacity = 100,
            PerPhoneRefillPerSecond = 100,
            PerCalendarCapacity = 4,
            PerCalendarRefillPerSecond = 0.001,
        };

        var outcomes = new List<BookingOutcome>();
        for (var i = 0; i < slots.Count; i++)
        {
            outcomes.Add(await BookAsync(seed, slots[i].Id, $"+799940000{i:D2}", options));
        }

        Assert.Equal(4, outcomes.Count(outcome => outcome.IsSuccess));
        Assert.All(
            outcomes.Where(outcome => !outcome.IsSuccess),
            outcome => Assert.Equal("booking.rate_limited", outcome.Error!.Value.Code));
    }

    [Fact]
    public async Task TwoTenantsSharingOnePhoneNumber_DoNotShareABucket()
    {
        // One person booking at two shops in the same minute is ordinary behaviour. A bucket keyed on
        // the number alone would let one shop's customer throttle another's.
        var mine = await SeedAsync();
        var theirs = await SeedAsync();
        var mySlots = await AvailableSlotsAsync(mine, 3);
        var theirSlots = await AvailableSlotsAsync(theirs, 1);

        var options = new BookingRateLimitOptions
        {
            PerPhoneCapacity = 2,
            PerPhoneRefillPerSecond = 0.001,
            PerCalendarCapacity = 10_000,
            PerCalendarRefillPerSecond = 10_000,
        };

        const string phone = "+79995000001";
        Assert.True((await BookAsync(mine, mySlots[0].Id, phone, options)).IsSuccess);
        Assert.True((await BookAsync(mine, mySlots[1].Id, phone, options)).IsSuccess);

        // Third at the first shop: over budget.
        Assert.False((await BookAsync(mine, mySlots[2].Id, phone, options)).IsSuccess);

        // Same number, different tenant, untouched budget.
        Assert.True((await BookAsync(theirs, theirSlots[0].Id, phone, options)).IsSuccess);
    }

    private async Task<BookingOutcome> BookAsync(
        SeededCalendar seed, EventId eventId, string phone, BookingRateLimitOptions options)
    {
        await using var db = fixture.CreateDbContext();

        var handler = new BookEventHandler(
            new BookingCalendarRepository(db),
            new TenantRepository(db),
            new EventRepository(db),
            new WorkerRepository(db),
            new ServiceRepository(db),
            new BookingStore(db),
            new RedisRateLimiter(
                fixture.RedisMultiplexer,
                new ResiliencePipelineBuilder().AddTimeout(TimeSpan.FromSeconds(5)).Build(),
                NullLogger<RedisRateLimiter>.Instance),
            options,
            new BookingOptions(),
            new UuidV7Generator(),
            new FixedClock(Now));

        return await handler.HandleAsync(
            new BookEvent(seed.CalendarId, eventId, seed.ServiceId, phone, "Anna"), CancellationToken.None);
    }

    private async Task<IReadOnlyList<Event>> AvailableSlotsAsync(SeededCalendar seed, int count)
    {
        var slots = new List<Event>();
        for (var i = 0; i < count; i++)
        {
            var start = Now.AddHours(2 + i);
            slots.Add(Event.Materialize(
                new EventId(NewId()), seed.TenantId, seed.CalendarId, seed.WorkerId,
                new TimeSlot(start, start.AddMinutes(45)), DateOnly.FromDateTime(start.UtcDateTime), Now));
        }

        await using var db = fixture.CreateDbContext();
        await new EventRepository(db).AddRangeAsync(slots, CancellationToken.None);
        return slots;
    }

    private async Task<SeededCalendar> SeedAsync()
    {
        var tenant = Tenant.Register(new TenantId(NewId()), "Barbershop", new TenantPublicKey("shop-" + NewId().ToString("N")), Now);
        var calendar = BookingCalendar.Create(
            new CalendarId(NewId()), tenant.Id, "Main", new CalendarTimeZone("Europe/Moscow"), 10, Now);
        var worker = Worker.Create(new WorkerId(NewId()), tenant.Id, "Alex");
        var service = Service.Create(new ServiceId(NewId()), tenant.Id, "Haircut", TimeSpan.FromMinutes(45));

        calendar.Publish();
        worker.JoinCalendar(calendar);
        worker.Offer(service);

        await using var db = fixture.CreateDbContext();
        db.Tenants.Add(tenant);
        db.Calendars.Add(calendar);
        db.Services.Add(service);
        db.Workers.Add(worker);
        await db.SaveChangesAsync();

        return new SeededCalendar(tenant.Id, calendar.Id, worker.Id) { ServiceId = service.Id };
    }

    private static Guid NewId() => Guid.CreateVersion7(DateTimeOffset.UtcNow);
}
