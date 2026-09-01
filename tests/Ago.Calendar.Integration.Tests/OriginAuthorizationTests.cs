using System.Net;
using System.Net.Http.Json;
using Ago.Calendar.Contracts;
using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres;
using Microsoft.AspNetCore.Hosting;

namespace Ago.Calendar.Integration.Tests;

/// <summary>
/// <b>Layer 2</b> of `5-01`'s two-layer CORS model, over real HTTP against the real host: an origin
/// that <see cref="TenantOriginCorsPolicyProviderTests"/> has just shown layer 1 <i>would</i> allow,
/// used against the tenant that never approved it.
///
/// <para><b>This is `20-06`'s own Done-when clause about being tricked</b>, and it is the test that
/// would still pass if layer 1 were deleted - which is exactly why it exists separately. A suite that
/// only covered layer 1 would go green with no tenant boundary at all.</para>
///
/// <para>Both tenants are real and both origins are real: "tenant B's origin" has to be an origin
/// some tenant genuinely lists, or the test would be proving layer 1 again under a different
/// name.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public class OriginAuthorizationTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string OriginA = "https://tenant-a.example";
    private const string OriginB = "https://tenant-b.example";

    private OriginAuthorizationApiFactory _factory = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _factory = new OriginAuthorizationApiFactory(fixture);
        _client = _factory.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task TheBookingSurface_IsServed_ToTheOriginItsOwnTenantApproved()
    {
        var (a, _) = await TwoTenantsAsync();

        var response = await GetAsync($"/api/v1/embed/{a.Tenant.PublicKey.Value}", OriginA);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var surface = await response.Content.ReadFromJsonAsync<BookingSurfaceResponse>();
        Assert.Equal(a.Calendar.Id.Value, Assert.Single(surface!.Calendars).CalendarId);
    }

    [Fact]
    public async Task TheBookingSurface_IsRefused_ToAnotherTenantsApprovedOrigin()
    {
        var (a, _) = await TwoTenantsAsync();

        // OriginB is in *some* tenant's list, so layer 1 hands the browser a matching
        // Access-Control-Allow-Origin and the page's JavaScript is allowed to read whatever comes
        // back. What comes back is a 404.
        var response = await GetAsync($"/api/v1/embed/{a.Tenant.PublicKey.Value}", OriginB);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains(
            "booking.origin_not_allowed", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSlotList_IsRefused_ToAnotherTenantsApprovedOrigin()
    {
        var (a, _) = await TwoTenantsAsync();

        var url =
            $"/api/v1/embed/{a.Tenant.PublicKey.Value}/calendars/{a.Calendar.Id.Value}/slots" +
            $"?serviceId={a.Service.Id.Value}";

        Assert.Equal(HttpStatusCode.OK, (await GetAsync(url, OriginA)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(url, OriginB)).StatusCode);
    }

    [Fact]
    public async Task TheWorkerList_IsRefused_ToAnotherTenantsApprovedOrigin()
    {
        var (a, _) = await TwoTenantsAsync();

        var url =
            $"/api/v1/embed/{a.Tenant.PublicKey.Value}/calendars/{a.Calendar.Id.Value}/workers" +
            $"?serviceId={a.Service.Id.Value}";

        Assert.Equal(HttpStatusCode.OK, (await GetAsync(url, OriginA)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(url, OriginB)).StatusCode);
    }

    [Fact]
    public async Task ABooking_IsRefused_WhenTheOriginBelongsToAnotherTenant()
    {
        // The write path, which is the one that matters most: the surface reads leak nothing worse
        // than a shop's own published staff list, and this one creates a row.
        var (a, _) = await TwoTenantsAsync();
        var slot = await ABookableSlotAsync(a);

        var refused = await BookAsync(a, slot, OriginB, "+79996100001");

        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);

        // And nothing was written - the slot is still there for a legitimate customer.
        await using var db = fixture.CreateDbContext();
        Assert.Equal(
            EventStatus.Available,
            (await db.Events.FindAsync(slot.Id))!.Status);
    }

    [Fact]
    public async Task ABooking_Succeeds_FromTheOriginItsOwnTenantApproved()
    {
        var (a, _) = await TwoTenantsAsync();
        var slot = await ABookableSlotAsync(a);

        Assert.Equal(HttpStatusCode.OK, (await BookAsync(a, slot, OriginA, "+79996100002")).StatusCode);
    }

    [Fact]
    public async Task ABooking_WithNoOriginHeader_StillSucceeds()
    {
        // The deliberate asymmetry, proved end to end so nobody re-derives it from the aggregate. See
        // OriginPolicy: a caller with no Origin is not a browser, `21-01`'s channel adapter is such a
        // caller, and Origin is forgeable by anything that is not a browser anyway.
        var (a, _) = await TwoTenantsAsync();
        var slot = await ABookableSlotAsync(a);

        Assert.Equal(HttpStatusCode.OK, (await BookAsync(a, slot, origin: null, "+79996100003")).StatusCode);
    }

    private async Task<(SeededTenant A, SeededTenant B)> TwoTenantsAsync()
    {
        var a = await CalendarSeed.WriteAsync(fixture, allowedOrigins: [OriginA]);
        var b = await CalendarSeed.WriteAsync(fixture, allowedOrigins: [OriginB]);
        return (a, b);
    }

    private async Task<Event> ABookableSlotAsync(SeededTenant seed)
    {
        // `20-18`: BookEventHandler now run-finds through the worker's own schedule.
        await CalendarSeed.AddWeeklyScheduleAsync(fixture, seed, horizonDays: 30);

        // Far enough ahead that the claim's own `starts_at > now` predicate holds against the host's
        // real clock - this test cannot inject a fake one, which is what makes it a check on the
        // wiring rather than on the logic.
        var slot = CalendarSeed.Slot(seed, DateTimeOffset.UtcNow.AddDays(9));

        await using var db = fixture.CreateDbContext();
        await new EventRepository(db).AddRangeAsync([slot], CancellationToken.None);
        return slot;
    }

    private async Task<HttpResponseMessage> GetAsync(string url, string? origin)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }

        return await _client.SendAsync(request);
    }

    /// <summary><c>await</c> inside the <c>using</c>: returning the task would dispose the request -
    /// and its content stream - before TestHost had finished reading it.
    ///
    /// <para>`20-10`: pre-seeds an already-verified <see cref="Customer"/> row for <paramref name="seed"/>'s
    /// own tenant, the same returning-customer-shortcut approach <c>BookingEndpointTests.BookAsync</c>'s
    /// own remarks explain - this file's own concern is the origin boundary, not phone verification,
    /// and every booking call here now has to clear that gate regardless.</para>
    /// </summary>
    private async Task<HttpResponseMessage> BookAsync(SeededTenant seed, Event slot, string? origin, string phone)
    {
        var customer = Customer.Register(new CustomerId(Guid.CreateVersion7()), seed.Tenant.Id, new PhoneNumber(phone), CalendarSeed.Now);
        customer.RecordVerifiedPhone(CalendarSeed.Now);
        await using (var db = fixture.CreateDbContext())
        {
            db.Customers.Add(customer);
            await db.SaveChangesAsync();
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/calendars/{seed.Calendar.Id.Value}/events/{slot.Id.Value}/book")
        {
            Content = JsonContent.Create(new BookEventRequest(seed.Service.Id.Value, phone, "Anna")),
        };

        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }

        return await _client.SendAsync(request);
    }
}

/// <summary>
/// <see cref="CalendarApiFactory"/> with the public booking API turned on - this file's own concern is
/// the origin boundary, not the lockdown, and its booking tests need the real endpoint to answer. See
/// <c>PublicBookingApiLockdownTests</c> for the "closed by default" guarantee itself, proved against a
/// host that leaves this setting untouched.
/// </summary>
internal sealed class OriginAuthorizationApiFactory(PostgresFixture fixture) : CalendarApiFactory(fixture)
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.UseSetting("PublicBookingApi:Enabled", "true");
    }
}
