using System.Net;
using System.Net.Http.Json;
using Ago.Calendar.Contracts;
using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;

namespace Ago.Calendar.Integration.Tests;

/// <summary>
/// 2026-09-01: proves <c>PublicBookingApiGate</c> over real HTTP against the real host - the two
/// guarantees a unit test of <c>PublicBookingApiOptions.Enabled</c>'s own boolean could not prove: that
/// an out-of-the-box host, with nothing set for <c>PublicBookingApi:*</c> at all, refuses every route
/// the gate is attached to before any handler runs and writes nothing; and that flipping the one
/// setting back on is genuinely enough to reopen the identical surface `20-10` already built and tested
/// elsewhere (<c>BookingEndpointTests</c>, <c>PhoneVerificationEndpointTests</c>).
///
/// <para><b>Deliberately the plain <see cref="CalendarApiFactory"/>, not a subclass</b> - every other
/// factory in this project that hits these routes now opts in with its own
/// <c>PublicBookingApi:Enabled=true</c> override (see each factory's own remarks); this file is the one
/// place that must not, because the whole guarantee under test is what an untouched configuration
/// does.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public class PublicBookingApiLockdownTests(PostgresFixture fixture) : IAsyncLifetime
{
    private CalendarApiFactory _factory = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _factory = new CalendarApiFactory(fixture);
        _client = _factory.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task TheBookingEndpoint_Refuses403_AndWritesNothing_WhenTheFlagIsOffByDefault()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        await CalendarSeed.AddWeeklyScheduleAsync(fixture, seed, horizonDays: 30);
        var slot = CalendarSeed.Slot(seed, DateTimeOffset.UtcNow.AddDays(7));
        await using (var db = fixture.CreateDbContext())
        {
            await new EventRepository(db).AddRangeAsync([slot], CancellationToken.None);
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"/api/v1/calendars/{seed.Calendar.Id.Value}/events/{slot.Id.Value}/book")
        {
            Content = JsonContent.Create(new BookEventRequest(seed.Service.Id.Value, "+79997000001", "Anna")),
        };

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("booking.public_api_disabled", body, StringComparison.Ordinal);

        // The gate ran before BookEventHandler ever touched the row - the slot is still exactly what
        // it was before the request, not merely "not confirmed".
        await using var verify = fixture.CreateDbContext();
        Assert.Equal(EventStatus.Available, (await verify.Events.SingleAsync(e => e.Id == slot.Id)).Status);
    }

    [Fact]
    public async Task TheInitiatePhoneVerificationEndpoint_Refuses403_WhenTheFlagIsOffByDefault()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/calendars/{seed.Calendar.Id.Value}/phone-verifications",
            new InitiatePhoneVerificationRequest("+79997000002"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains(
            "booking.public_api_disabled", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheConfirmPhoneVerificationEndpoint_Refuses403_WhenTheFlagIsOffByDefault()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);

        // No PendingPhoneVerification exists for this id at all - if the gate did not run first, this
        // would be phone_verification.not_found (404), not the lockdown's own 403. Getting 403 here is
        // exactly what proves the gate sits ahead of ConfirmPhoneVerificationHandler rather than beside
        // it.
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/calendars/{seed.Calendar.Id.Value}/phone-verifications/{Guid.NewGuid()}/confirm",
            new ConfirmPhoneVerificationRequest("000000"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains(
            "booking.public_api_disabled", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheBookingEndpoint_Succeeds_OnceTheFlagIsFlippedBackOn()
    {
        // The reversibility guarantee itself: the identical host, the identical route, the identical
        // `20-10` code underneath - the only thing that changed between this test and the first one
        // above is one configuration value.
        await using var reopened = new PublicBookingApiReopenedFactory(fixture);
        using var client = reopened.CreateClient();

        var seed = await CalendarSeed.WriteAsync(fixture);
        await CalendarSeed.AddWeeklyScheduleAsync(fixture, seed, horizonDays: 30);
        var slot = CalendarSeed.Slot(seed, DateTimeOffset.UtcNow.AddDays(7));
        await using (var db = fixture.CreateDbContext())
        {
            await new EventRepository(db).AddRangeAsync([slot], CancellationToken.None);
            var customer = Customer.Register(
                new CustomerId(Guid.CreateVersion7()), seed.Tenant.Id, new PhoneNumber("+79997000003"), CalendarSeed.Now);
            customer.RecordVerifiedPhone(CalendarSeed.Now);
            db.Customers.Add(customer);
            await db.SaveChangesAsync();
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"/api/v1/calendars/{seed.Calendar.Id.Value}/events/{slot.Id.Value}/book")
        {
            Content = JsonContent.Create(new BookEventRequest(seed.Service.Id.Value, "+79997000003", "Anna")),
        };

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}

/// <summary><see cref="CalendarApiFactory"/> with the lockdown explicitly flipped back on - used by
/// exactly one test above, to keep every other test in this file proving the untouched default.</summary>
internal sealed class PublicBookingApiReopenedFactory(PostgresFixture fixture) : CalendarApiFactory(fixture)
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.UseSetting("PublicBookingApi:Enabled", "true");
    }
}
