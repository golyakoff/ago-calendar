using System.Net;
using System.Net.Http.Json;
using Ago.Calendar.Contracts;
using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Ago.Calendar.Integration.Tests;

/// <summary>
/// The booking endpoint over real HTTP, against the real <c>Ago.Calendar.Api</c> host, a real
/// Postgres and a real Redis.
///
/// <para><b>Why this exists on top of the handler tests.</b> Two of `20-03`'s Done-when clauses are
/// claims about a *response*: that a rate-limited caller gets <c>429</c> with a <c>Retry-After</c>
/// header, and that a lost race is not a 500. Neither is provable from a handler's return value -
/// a status code and a header only exist once something has mapped an outcome onto them, and that
/// mapping (<c>ErrorExtensions.ToProblem</c>, and the three lines of endpoint that call it) is
/// exactly the kind of glue that compiles while being wrong.</para>
///
/// <para>It also proves the host composes at all: <c>CalendarModule</c> now wires Postgres, the tz
/// resolver, Redis and four handlers, and a <c>BackgroundService</c>-free API host that cannot build
/// its service provider fails for the first time in a container otherwise.</para>
///
/// <para>Phone numbers are invented <c>+7999...</c> values belonging to nobody
/// (<c>personal-data.md</c>).</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public class BookingEndpointTests(PostgresFixture fixture) : IAsyncLifetime
{
    private BookingApiFactory _factory = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _factory = new BookingApiFactory(fixture);
        _client = _factory.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task ASuccessfulBooking_Returns200AndTellsTheCustomerNothingAboutPendingState()
    {
        var (seed, slot) = await ABookableSlotAsync();

        var response = await BookAsync(seed, slot.Id, "+79996000001");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The literal bytes on the wire, not a deserialised view of them: the product's central
        // design decision is that the customer is told they are booked, and a "pending" anywhere in
        // this payload would be the reversal of it arriving unnoticed.
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("ending", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("deadline", body, StringComparison.OrdinalIgnoreCase);

        var confirmed = await response.Content.ReadFromJsonAsync<BookingConfirmedResponse>();
        Assert.Equal(slot.Id.Value, confirmed!.BookingId);
        Assert.Equal(seed.Worker.Id.Value, confirmed.WorkerId);

        // Meanwhile the row did move to PendingConfirmation with a deadline - the two-step mechanic,
        // observed from both sides in one test so the pair cannot silently drift apart.
        await using var db = fixture.CreateDbContext();
        var stored = await db.Events.SingleAsync(e => e.Id == slot.Id);
        Assert.Equal(EventStatus.PendingConfirmation, stored.Status);
        Assert.NotNull(stored.ConfirmationDeadline);
    }

    [Fact]
    public async Task ALostRace_Returns409AndNotA500()
    {
        var (seed, slot) = await ABookableSlotAsync();

        Assert.Equal(HttpStatusCode.OK, (await BookAsync(seed, slot.Id, "+79996000002")).StatusCode);

        var second = await BookAsync(seed, slot.Id, "+79996000003");

        // 409, and a problem-details body a client can branch on. A 500 here would be the thing
        // `4-01` warned about in as many words: an ordinary lost race dressed up as a fault.
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        var problem = await second.Content.ReadAsStringAsync();
        Assert.Contains("booking.slot_unavailable", problem, StringComparison.Ordinal);
        Assert.Contains("traceId", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADeniedBooking_Returns429WithARetryAfterHeader()
    {
        // The Done-when's own words: "a denied booking attempt returns 429/Retry-After, not a bare
        // rejection with no guidance." The factory below configures a per-phone capacity of 2 with a
        // refill slow enough that nothing comes back during the test.
        var (seed, slots) = await BookableSlotsAsync(4);
        const string phone = "+79996000004";

        Assert.Equal(HttpStatusCode.OK, (await BookAsync(seed, slots[0].Id, phone)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await BookAsync(seed, slots[1].Id, phone)).StatusCode);

        var denied = await BookAsync(seed, slots[2].Id, phone);

        Assert.Equal(HttpStatusCode.TooManyRequests, denied.StatusCode);

        var retryAfter = Assert.Single(denied.Headers.GetValues("Retry-After"));
        Assert.True(int.TryParse(retryAfter, out var seconds), $"Retry-After must be a delta-seconds integer; got '{retryAfter}'.");
        Assert.True(seconds >= 1, "Retry-After must tell the caller to wait at least a second, never zero.");

        // And nothing was written: the third slot is still bookable by somebody else.
        await using var db = fixture.CreateDbContext();
        Assert.Equal(EventStatus.Available, (await db.Events.SingleAsync(e => e.Id == slots[2].Id)).Status);
    }

    [Fact]
    public async Task AMalformedPhone_Returns400()
    {
        var (seed, slot) = await ABookableSlotAsync();

        var response = await BookAsync(seed, slot.Id, "12345");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("booking.invalid_phone", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnknownCalendar_Returns404()
    {
        var (seed, slot) = await ABookableSlotAsync();

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/calendars/{Guid.NewGuid()}/events/{slot.Id.Value}/book",
            new BookEventRequest(seed.Service.Id.Value, "+79996000005", null));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task TheEndpointRequiresNoAuthentication()
    {
        // Stated as a test because it is a deliberate design decision, not an omission: a customer
        // books with a phone number and no account (Customer has no password by design), so there is
        // nothing to authenticate. What stands in for it is the calendar id binding into the claim's
        // own WHERE clause plus two rate-limit buckets - see BookingEndpoints.
        var (seed, slot) = await ABookableSlotAsync();

        Assert.DoesNotContain(_client.DefaultRequestHeaders, header => header.Key == "Authorization");
        Assert.Equal(HttpStatusCode.OK, (await BookAsync(seed, slot.Id, "+79996000006")).StatusCode);
    }

    private Task<HttpResponseMessage> BookAsync(SeededTenant seed, EventId eventId, string phone) =>
        _client.PostAsJsonAsync(
            $"/api/v1/calendars/{seed.Calendar.Id.Value}/events/{eventId.Value}/book",
            new BookEventRequest(seed.Service.Id.Value, phone, "Anna"));

    private async Task<(SeededTenant Seed, Event Slot)> ABookableSlotAsync()
    {
        var (seed, slots) = await BookableSlotsAsync(1);
        return (seed, slots[0]);
    }

    private async Task<(SeededTenant Seed, IReadOnlyList<Event> Slots)> BookableSlotsAsync(int count)
    {
        var seed = await CalendarSeed.WriteAsync(fixture);

        // `20-18`: BookEventHandler now run-finds through the worker's own schedule -
        // AddWeeklyScheduleAsync's own default (45 minutes, matching CalendarSeed.Slot's own default
        // duration) keeps every booking here the single-slot case this file's own concern (the HTTP
        // wiring, not run-finding) needs.
        await CalendarSeed.AddWeeklyScheduleAsync(fixture, seed, horizonDays: 30);

        // Far enough in the future that the claim's own `starts_at > now` predicate is satisfied
        // against the host's real clock - this test cannot inject a fake one, which is precisely
        // what makes it a check on the wiring rather than on the logic.
        var slots = new List<Event>();
        for (var i = 0; i < count; i++)
        {
            slots.Add(CalendarSeed.Slot(seed, DateTimeOffset.UtcNow.AddDays(7).AddHours(i)));
        }

        await using var db = fixture.CreateDbContext();
        await new EventRepository(db).AddRangeAsync(slots, CancellationToken.None);
        return (seed, slots);
    }
}

/// <summary>
/// <see cref="CalendarApiFactory"/> with a small per-phone bucket that does not refill during a test,
/// so "the third attempt is denied" is deterministic rather than a race against the clock. The
/// calendar bucket stays wide so it never interferes; its own behaviour is proven separately, against
/// a real Redis, in <c>Ago.Calendar.Concurrency.Tests</c>.
/// </summary>
internal sealed class BookingApiFactory(PostgresFixture fixture) : CalendarApiFactory(fixture)
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.UseSetting("BookingRateLimit:PerPhoneCapacity", "2");
        builder.UseSetting("BookingRateLimit:PerPhoneRefillPerSecond", "0.001");
    }
}
