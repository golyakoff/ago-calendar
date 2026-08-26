using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Application.UseCases.PublicBooking;
using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Tests;

/// <summary>
/// `20-06`'s unauthenticated read surface, and specifically <b>layer 2</b> of `5-01`'s two-layer CORS
/// model: once a request has resolved which tenant it is for, its <c>Origin</c> is compared against
/// <i>that</i> tenant's list.
///
/// <para>These are handler tests with fakes; the layer-1 half is untestable at this level by
/// construction - a CORS policy is a thing an HTTP pipeline evaluates - and is proved against a real
/// Postgres in <c>Ago.Calendar.Integration.Tests</c>.</para>
/// </summary>
public class PublicBookingSurfaceTests
{
    private const string Approved = "https://shop.example";
    private const string SomebodyElses = "https://other.example";

    [Fact]
    public async Task TheSurface_IsReturned_ForAnApprovedOrigin()
    {
        var world = new World();

        var result = await world.SurfaceAsync(origin: Approved);

        Assert.True(result.IsSuccess);
        Assert.Equal("Barbershop", result.Value.TenantName);
        var calendar = Assert.Single(result.Value.Calendars);
        Assert.Equal(BookingFixtures.CalendarId, calendar.CalendarId);
        Assert.Equal("Haircut", Assert.Single(calendar.Services).Name);
    }

    [Fact]
    public async Task TheSurface_IsRefused_ForAnOriginThisTenantNeverApproved()
    {
        // The layer-2 case in its purest form: the origin belongs to *a* tenant (layer 1 would let the
        // browser read the response), and it is not this one's.
        var world = new World();

        var result = await world.SurfaceAsync(origin: SomebodyElses);

        Assert.True(result.IsFailure);
        Assert.Equal("booking.origin_not_allowed", result.Error!.Value.Code);
    }

    [Fact]
    public async Task ARefusedOrigin_IsToldExactlyWhatAnUnknownKeyIsTold()
    {
        // Same words, different code. A caller must not be able to tell "that tenant does not exist"
        // from "that tenant exists and you are not allowed to embed it" - the second answer confirms
        // the first. The code exists only so this product's own logs and tests can tell them apart.
        var world = new World();

        var refused = await world.SurfaceAsync(origin: SomebodyElses);
        var unknown = await world.SurfaceAsync(publicKey: "nobody-here", origin: Approved);

        Assert.Equal(refused.Error!.Value.Message, unknown.Error!.Value.Message);
        Assert.NotEqual(refused.Error!.Value.Code, unknown.Error!.Value.Code);
    }

    [Fact]
    public async Task TheSurface_IsReturned_WhenThereIsNoOriginHeaderAtAll()
    {
        // Deliberate, and the opposite of what AGO Chat's visitor-session endpoint does - see
        // OriginPolicy. A channel adapter with no browser in the path (`21-01`) is a legitimate
        // caller here, and Origin is forgeable by any non-browser caller anyway, so requiring it
        // would ban the product's own second channel to gain nothing.
        var world = new World();

        Assert.True((await world.SurfaceAsync(origin: null)).IsSuccess);
    }

    [Fact]
    public async Task AMalformedPublicKey_IsNotFoundRatherThanAnException()
    {
        // "Shop Key" cannot be a TenantPublicKey at all, so the resolver never reaches the database.
        // Letting the constructor's ArgumentException escape would make a stranger's typo a 500.
        var world = new World();

        var result = await world.SurfaceAsync(publicKey: "Shop Key", origin: Approved);

        Assert.True(result.IsFailure);
        Assert.Equal("booking.surface_not_found", result.Error!.Value.Code);
    }

    [Fact]
    public async Task AnUnpublishedCalendar_IsNotFound()
    {
        var world = new World(calendarPublished: false);

        var result = await world.SlotsAsync(origin: Approved);

        Assert.True(result.IsFailure);
        Assert.Equal("booking.surface_not_found", result.Error!.Value.Code);
    }

    [Fact]
    public async Task AnotherTenantsCalendarId_IsNotFound()
    {
        // The id exists; it is simply not this tenant's. Guessing a uuid must not reach another
        // shop's availability.
        var world = new World();

        var result = await world.SlotsAsync(calendarId: Guid.NewGuid(), origin: Approved);

        Assert.True(result.IsFailure);
        Assert.Equal("booking.surface_not_found", result.Error!.Value.Code);
    }

    [Fact]
    public async Task Slots_AreReadFromNowOnwardAndClampedToThePageBound()
    {
        var world = new World();

        var result = await world.SlotsAsync(origin: Approved, limit: 100_000);

        Assert.True(result.IsSuccess);
        var query = Assert.Single(world.ReadStore.SlotQueries);
        Assert.Equal(BookingFixtures.Now, query.NotBefore);
        Assert.Equal(GetOpenSlotsHandler.MaxLimit, query.Limit);
        Assert.Null(query.WorkerId);
    }

    [Fact]
    public async Task Workers_AreScopedToTheCalendarTheResolverApproved()
    {
        var world = new World();
        world.ReadStore.Workers.Add(new BookableWorkerRow(BookingFixtures.WorkerId, "Alex"));

        var result = await world.WorkersAsync(origin: Approved);

        Assert.True(result.IsSuccess);
        Assert.Equal("Alex", Assert.Single(result.Value).DisplayName);
        Assert.Equal(BookingFixtures.CalendarId, Assert.Single(world.ReadStore.AskedFor));
    }

    private sealed class World
    {
        private readonly EmbedScopeResolver _resolver;

        public World(bool calendarPublished = true)
        {
            var tenant = BookingFixtures.Tenant([Approved]);
            var calendar = BookingFixtures.Calendar(calendarPublished);

            _resolver = new EmbedScopeResolver(
                new FakeTenantRepository(tenant), new FakeCalendarRepository(calendar));

            ReadStore.Services.Add(new BookableServiceRow(BookingFixtures.ServiceId, "Haircut", 45));
        }

        public FakeBookingSurfaceReadStore ReadStore { get; } = new();

        public Task<Ago.Platform.Kernel.Result<BookingSurface>> SurfaceAsync(
            string publicKey = "barbershop", string? origin = null) =>
            new GetBookingSurfaceHandler(
                    _resolver, new FakeCalendarRepository(BookingFixtures.Calendar()), ReadStore)
                .HandleAsync(new GetBookingSurface(publicKey, origin), CancellationToken.None);

        public Task<Ago.Platform.Kernel.Result<IReadOnlyList<BookableWorkerRow>>> WorkersAsync(
            string? origin = null) =>
            new GetBookableWorkersHandler(_resolver, ReadStore).HandleAsync(
                new GetBookableWorkers(
                    "barbershop", BookingFixtures.CalendarId.Value, BookingFixtures.ServiceId.Value, origin),
                CancellationToken.None);

        public Task<Ago.Platform.Kernel.Result<IReadOnlyList<OpenSlotRow>>> SlotsAsync(
            Guid? calendarId = null, string? origin = null, int limit = 10) =>
            new GetOpenSlotsHandler(_resolver, ReadStore, new FakeClock(BookingFixtures.Now)).HandleAsync(
                new GetOpenSlots(
                    "barbershop",
                    calendarId ?? BookingFixtures.CalendarId.Value,
                    BookingFixtures.ServiceId.Value,
                    null,
                    limit,
                    origin),
                CancellationToken.None);
    }
}
