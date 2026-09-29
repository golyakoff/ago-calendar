using System.Text.Json;
using Ago.Calendar.Application.UseCases.ManualBooking;
using Ago.Calendar.Contracts;
using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Tests;

/// <summary>
/// `26-268`/`adr/0188`: the operator manual-entry handler, with every port faked. Which failure each
/// refusal reports, which refusals never reach the store at all, and - the assertions that matter
/// most - that a successful entry stages exactly two events (<c>PersonRegistered</c>,
/// <c>BookingConfirmed</c>) with a freshly minted person id, and that a lost claim race is an ordinary
/// rejection rather than a fault. `26-268`§2a adds the reuse branch: a valid, same-tenant
/// <c>ReusePersonId</c> claims under that id and stages no <c>PersonRegistered</c>; an invalid one (wrong
/// tenant, or no such id) is refused before the store is ever reached.
/// </summary>
public class EnterManualBookingHandlerTests
{
    private static readonly OperatorId Operator = new(new Guid("66666666-6666-6666-6666-666666666666"));

    [Fact]
    public async Task ASuccessfulEntry_ClaimsTheRun_AndStagesExactlyPersonRegisteredAndBookingConfirmed()
    {
        var world = new World();

        var result = await world.HandleAsync(Command());

        Assert.True(result.IsSuccess);
        Assert.Equal(BookingFixtures.EventId, result.Value.BookingId);

        var attempt = Assert.Single(world.Store.Attempts);
        Assert.Equal([BookingFixtures.EventId], attempt.EventIds);
        Assert.Equal(BookingFixtures.CalendarId, attempt.CalendarId);
        Assert.Equal(BookingFixtures.ServiceId, attempt.ServiceId);
        Assert.Equal(BookingFixtures.Phone, attempt.Phone.Value);

        // A fresh person every time - `adr/0188`/§3.4 defers phone-based recognition to its own slice.
        Assert.NotEqual(Guid.Empty, attempt.PersonId);

        // Exactly the two events `adr/0188`/§3.5 names, and no third - never
        // BookingPendingStateChanged, since this booking was never pending. Non-null on the mint path -
        // `26-268`§2a's own reuse path is what leaves this null, and this test never reuses.
        Assert.NotNull(attempt.PersonRegisteredEvent);
        var personRegisteredEvent = attempt.PersonRegisteredEvent!;
        Assert.Equal("PersonRegistered", personRegisteredEvent.Type);
        Assert.Equal("BookingConfirmed", attempt.BookingConfirmedEvent.Type);

        var registered = JsonSerializer.Deserialize<PersonRegistered>(personRegisteredEvent.Payload)!;
        Assert.Equal(BookingFixtures.Phone, registered.Phone);
        Assert.Equal(attempt.PersonId, registered.PersonId);
        Assert.Equal("Anna", registered.Name);

        var confirmed = JsonSerializer.Deserialize<BookingConfirmed>(attempt.BookingConfirmedEvent.Payload)!;
        Assert.Equal(BookingFixtures.EventId.Value, confirmed.EventId);
        Assert.Equal(attempt.PersonId, confirmed.PersonId);
    }

    [Fact]
    public async Task ABlankDisplayName_IsNormalisedToNull_InThePersonRegisteredPayload()
    {
        var world = new World();

        await world.HandleAsync(Command(displayName: "   "));

        var attempt = Assert.Single(world.Store.Attempts);
        Assert.NotNull(attempt.PersonRegisteredEvent);
        var registered = JsonSerializer.Deserialize<PersonRegistered>(attempt.PersonRegisteredEvent!.Payload)!;
        Assert.Null(registered.Name);
    }

    [Fact]
    public async Task WithoutThePermission_IsRefusedAndTouchesNothing()
    {
        var world = new World();
        world.Permissions.Deny(Permission.BookingCreate);

        var result = await world.HandleAsync(Command());

        Assert.True(result.IsFailure);
        Assert.Equal("booking.forbidden", result.Error!.Value.Code);
        Assert.False(world.Store.Attempts.Count > 0);
    }

    [Fact]
    public async Task ItsOwnPermission_IsNotSatisfiedByAnUnrelatedOne()
    {
        // adr/0016's granularity argument, held as a test: a tenant may grant reschedule without
        // create, so holding an unrelated permission is not enough.
        var world = new World();
        world.Permissions.Deny(Permission.BookingCreate);
        world.Permissions.Allow(Permission.BookingReschedule);

        var result = await world.HandleAsync(Command());

        Assert.Equal("booking.forbidden", result.Error!.Value.Code);
        Assert.Empty(world.Store.Attempts);
    }

    [Fact]
    public async Task AMalformedPhone_IsRejectedBeforeTheStoreIsReached()
    {
        var world = new World();

        var result = await world.HandleAsync(Command(phone: "12345"));

        Assert.Equal("booking.invalid_phone", result.Error!.Value.Code);
        Assert.Empty(world.Store.Attempts);
    }

    [Fact]
    public async Task AMissingCalendar_IsRejected()
    {
        var world = new World(calendarExists: false);

        var result = await world.HandleAsync(Command());

        Assert.Equal("booking.calendar_not_found", result.Error!.Value.Code);
        Assert.Empty(world.Store.Attempts);
    }

    [Fact]
    public async Task ACalendarBelongingToAnotherTenant_IsReportedAsAbsent()
    {
        // The cross-tenant guard every other lifecycle handler applies: an operator of tenant A must
        // not learn that a calendar id belongs to tenant B.
        var world = new World(calendar: OtherTenantsCalendar());

        var result = await world.HandleAsync(Command());

        Assert.Equal("booking.calendar_not_found", result.Error!.Value.Code);
        Assert.Empty(world.Store.Attempts);
    }

    [Fact]
    public async Task AWorkerWhoDoesNotOfferTheService_IsRejected()
    {
        var world = new World();
        var somethingElse = new ServiceId(new Guid("99999999-9999-9999-9999-999999999999"));

        var result = await world.HandleAsync(Command(serviceId: somethingElse));

        Assert.Equal("booking.service_not_offered", result.Error!.Value.Code);
        Assert.Empty(world.Store.Attempts);
    }

    [Fact]
    public async Task AnInactiveWorker_IsRejected()
    {
        var service = BookingFixtures.HaircutService();
        var world = new World(worker: BookingFixtures.WorkerOffering(service, active: false), service: service);

        var result = await world.HandleAsync(Command());

        Assert.Equal("booking.service_not_offered", result.Error!.Value.Code);
        Assert.Empty(world.Store.Attempts);
    }

    [Fact]
    public async Task AWorkerWithNoSchedule_IsRejected()
    {
        var world = new World(noSchedule: true);

        var result = await world.HandleAsync(Command());

        Assert.Equal("booking.service_not_offered", result.Error!.Value.Code);
        Assert.Empty(world.Store.Attempts);
    }

    [Fact]
    public async Task AStartEventIdOnAnotherWorker_IsReportedAsSlotUnavailable()
    {
        var otherWorkerSlot = Event.Materialize(
            BookingFixtures.EventId, BookingFixtures.TenantId, BookingFixtures.CalendarId,
            new WorkerId(Guid.NewGuid()), BookingFixtures.Slot, BookingFixtures.LocalDate, BookingFixtures.Now);
        var world = new World(day: [otherWorkerSlot]);

        var result = await world.HandleAsync(Command());

        Assert.Equal("booking.slot_unavailable", result.Error!.Value.Code);
        Assert.Empty(world.Store.Attempts);
    }

    [Fact]
    public async Task ABlockedStartSlot_FindsNoRun_AndIsReportedAsSlotUnavailable()
    {
        var blocked = Event.BlockOut(
            BookingFixtures.EventId, BookingFixtures.TenantId, BookingFixtures.CalendarId,
            BookingFixtures.WorkerId, BookingFixtures.Slot, BookingFixtures.LocalDate, BookingFixtures.Now);
        var world = new World(day: [blocked]);

        var result = await world.HandleAsync(Command());

        Assert.Equal("booking.slot_unavailable", result.Error!.Value.Code);
        Assert.Empty(world.Store.Attempts);
    }

    [Fact]
    public async Task AReuseOfAnExistingPersonInThisTenant_ClaimsUnderThatId_AndStagesNoPersonRegistered()
    {
        // `26-268`§2a/`adr/0188`: the operator chose «Это он» - the write must claim under the existing
        // id and must NOT announce a second registration for a person chat already knows about.
        var existing = PersonRecord.Register(
            new Guid("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), BookingFixtures.TenantId,
            new PhoneNumber(BookingFixtures.Phone), BookingFixtures.Now);
        var world = new World(existingPerson: existing);

        var result = await world.HandleAsync(Command(reusePersonId: existing.PersonId));

        Assert.True(result.IsSuccess, result.Error?.Message);
        var attempt = Assert.Single(world.Store.Attempts);
        Assert.Equal(existing.PersonId, attempt.PersonId);
        Assert.Equal(existing.PersonId, result.Value.PersonId);

        // Exactly BookingConfirmed - never PersonRegistered for an already-registered person.
        Assert.Null(attempt.PersonRegisteredEvent);
        Assert.Equal("BookingConfirmed", attempt.BookingConfirmedEvent.Type);
    }

    [Fact]
    public async Task AReuseOfAPersonBelongingToAnotherTenant_IsReportedAsNotFound_AndNeverReachesTheStore()
    {
        // The identical cross-tenant info-hiding shape every other lifecycle handler applies: an
        // operator of tenant A must not learn that a person id is real but belongs to tenant B.
        var otherTenantsPerson = PersonRecord.Register(
            new Guid("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            new TenantId(new Guid("77777777-7777-7777-7777-777777777777")),
            new PhoneNumber(BookingFixtures.Phone), BookingFixtures.Now);
        var world = new World(existingPerson: otherTenantsPerson);

        var result = await world.HandleAsync(Command(reusePersonId: otherTenantsPerson.PersonId));

        Assert.Equal("booking.person_not_found", result.Error!.Value.Code);
        Assert.Empty(world.Store.Attempts);
    }

    [Fact]
    public async Task AReuseOfAnUnknownPersonId_IsReportedAsNotFound_AndNeverReachesTheStore()
    {
        var world = new World();

        var result = await world.HandleAsync(Command(reusePersonId: Guid.NewGuid()));

        Assert.Equal("booking.person_not_found", result.Error!.Value.Code);
        Assert.Empty(world.Store.Attempts);
    }

    [Fact]
    public async Task ALostClaimRace_IsAnOrdinaryRejection_NotAnException()
    {
        var world = new World();
        world.Store.SlotIsClaimable = false;

        // No try/catch here, deliberately: a rows-affected count of zero is a normal outcome
        // (`4-01`'s own precedent), never an exception.
        var result = await world.HandleAsync(Command());

        Assert.False(result.IsSuccess);
        Assert.Equal("booking.slot_unavailable", result.Error!.Value.Code);

        // It still reached the store - losing is something only the database can determine.
        Assert.Single(world.Store.Attempts);
    }

    private static EnterManualBooking Command(
        string? phone = null, ServiceId? serviceId = null, string displayName = "Anna",
        Guid? reusePersonId = null) =>
        new(
            Operator, BookingFixtures.TenantId, BookingFixtures.CalendarId, serviceId ?? BookingFixtures.ServiceId,
            BookingFixtures.WorkerId, BookingFixtures.EventId, displayName, phone ?? BookingFixtures.Phone,
            reusePersonId);

    private static BookingCalendar OtherTenantsCalendar()
    {
        var calendar = BookingCalendar.Create(
            BookingFixtures.CalendarId,
            new TenantId(new Guid("77777777-7777-7777-7777-777777777777")),
            "Other", new CalendarTimeZone("Europe/Moscow"), BookingFixtures.Now);
        calendar.Publish();
        return calendar;
    }

    /// <summary>One world: the handler under test with every port faked, one available slot by
    /// default - the smallest world a manual entry needs.</summary>
    private sealed class World
    {
        private readonly EnterManualBookingHandler _handler;

        public World(
            BookingCalendar? calendar = null,
            bool calendarExists = true,
            Worker? worker = null,
            Service? service = null,
            WorkerSchedule? schedule = null,
            bool noSchedule = false,
            IReadOnlyList<Event>? day = null,
            PersonRecord? existingPerson = null)
        {
            var resolvedService = service ?? BookingFixtures.HaircutService();
            var resolvedCalendar = calendar ?? BookingFixtures.Calendar();
            var resolvedSchedule = noSchedule ? null : schedule ?? BookingFixtures.Schedule();
            var resolvedDay = day ?? [BookingFixtures.AvailableSlot()];
            PersonRecords = new FakePersonRecordRepository(existingPerson);

            _handler = new EnterManualBookingHandler(
                new FakeCalendarRepository(calendarExists ? resolvedCalendar : null),
                new FakeWorkerRepository(worker ?? BookingFixtures.WorkerOffering(resolvedService)),
                new FakeServiceRepository(resolvedService),
                new FakeWorkerScheduleRepository(resolvedSchedule),
                new FakeEventRepository(resolvedDay),
                PersonRecords,
                Store,
                Permissions,
                new SequentialIdGenerator(),
                new FakeClock(BookingFixtures.Now));
        }

        public FakeManualBookingStore Store { get; } = new();

        public FakePermissionChecker Permissions { get; } = new();

        /// <summary>`26-268`§2a: holds no record by default - the ordinary "no reuse in play" world
        /// every test before §2a already ran in, where <c>ReusePersonId</c> is always null and this port
        /// is never even asked. A test proving the reuse branch constructs a <c>World</c> with an
        /// existing record here first.</summary>
        public FakePersonRecordRepository PersonRecords { get; }

        public Task<Ago.Platform.Kernel.Result<Ago.Calendar.Application.Abstractions.BookingConfirmation>> HandleAsync(
            EnterManualBooking command) =>
            _handler.HandleAsync(command, CancellationToken.None);
    }
}
