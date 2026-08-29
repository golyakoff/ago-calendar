using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Application.UseCases.BookEvent;
using Ago.Calendar.Application.UseCases.ChatModuleTask;
using Ago.Calendar.Application.UseCases.PublicBooking;
using Ago.Calendar.Contracts;
using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Tests;

/// <summary>
/// `20-07`'s chat-module surface, driven step by step with every port faked - the same level
/// <c>BookEventHandlerTests</c> and <c>PublicBookingSurfaceTests</c> already prove the underlying
/// handlers at, reused here rather than re-faked: <c>StartModuleTaskHandler</c> and
/// <c>ReplyToModuleTaskHandler</c> depend on the real <see cref="GetBookingSurfaceHandler"/>,
/// <see cref="GetBookableWorkersHandler"/>, <see cref="GetOpenSlotsHandler"/> and
/// <see cref="BookEventHandler"/> instances, wired to the same fakes those handlers' own tests use.
/// </summary>
public class ChatModuleTaskHandlerTests
{
    [Fact]
    public async Task Start_OffersAChoiceListOfTheConfiguredCalendarsServices()
    {
        var world = new World();

        var result = await world.StartAsync();

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.Complete);
        var step = result.Value.Step;
        Assert.Equal(ModuleStepKind.ChoiceList, step.Kind);
        var action = Assert.Single(step.Actions);
        Assert.Equal(BookingFixtures.ServiceId.Value.ToString(), action.Value);
        Assert.Contains("Haircut", action.Label, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Start_WhenTheConfiguredCalendarDoesNotMatchAnyPublishedCalendar_IsNotConfigured()
    {
        var world = new World(configuredCalendarId: Guid.NewGuid());

        var result = await world.StartAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal("chat_module_task.not_configured", result.Error!.Value.Code);
    }

    [Fact]
    public async Task AFullWalkthrough_AllFiveSteps_EndsInACompletionCard()
    {
        var world = new World();

        var start = await world.StartAsync();
        var externalTaskId = start.Value.ExternalTaskId;

        var afterService = await world.ReplyAsync(
            externalTaskId, ModuleStepKinds.ChoiceList, BookingFixtures.ServiceId.Value.ToString());
        Assert.True(afterService.IsSuccess);
        Assert.False(afterService.Value.Complete);
        Assert.Equal(ModuleStepKind.ChoiceList, afterService.Value.Step!.Kind);
        var workerAction = Assert.Single(afterService.Value.Step!.Actions);
        Assert.Equal(BookingFixtures.WorkerId.Value.ToString(), workerAction.Value);

        var afterWorker = await world.ReplyAsync(externalTaskId, ModuleStepKinds.ChoiceList, workerAction.Value);
        Assert.True(afterWorker.IsSuccess);
        Assert.Equal(ModuleStepKind.DateTimePicker, afterWorker.Value.Step!.Kind);
        var slotAction = Assert.Single(afterWorker.Value.Step!.Actions);
        Assert.Equal(BookingFixtures.EventId.Value.ToString(), slotAction.Value);

        var afterSlot = await world.ReplyAsync(externalTaskId, ModuleStepKinds.DateTimePicker, slotAction.Value);
        Assert.True(afterSlot.IsSuccess);
        Assert.Equal(ModuleStepKind.Form, afterSlot.Value.Step!.Kind);
        Assert.Equal("phone", afterSlot.Value.Step!.FieldId);

        var afterPhone = await world.ReplyAsync(externalTaskId, ModuleStepKinds.Form, "+79990000001");
        Assert.True(afterPhone.IsSuccess);
        Assert.True(afterPhone.Value.Complete);
        Assert.Equal(ModuleStepKind.ConfirmationCard, afterPhone.Value.Step!.Kind);

        // The booking write actually happened - the fake records it exactly like
        // BookEventHandlerTests asserts against it.
        var attempt = Assert.Single(world.Bookings.Attempts);
        Assert.Equal(BookingFixtures.EventId, attempt.EventId);
        Assert.Equal("+79990000001", attempt.Phone.Value);
    }

    [Fact]
    public async Task ALostBookingRace_ReOffersFreshSlots_RatherThanADeadEnd()
    {
        var world = new World();
        world.Bookings.SlotIsClaimable = false;

        var start = await world.StartAsync();
        var externalTaskId = start.Value.ExternalTaskId;
        await world.ReplyAsync(
            externalTaskId, ModuleStepKinds.ChoiceList, BookingFixtures.ServiceId.Value.ToString());
        await world.ReplyAsync(
            externalTaskId, ModuleStepKinds.ChoiceList, BookingFixtures.WorkerId.Value.ToString());
        await world.ReplyAsync(
            externalTaskId, ModuleStepKinds.DateTimePicker, BookingFixtures.EventId.Value.ToString());

        var afterPhone = await world.ReplyAsync(externalTaskId, ModuleStepKinds.Form, "+79990000002");

        Assert.True(afterPhone.IsSuccess);
        Assert.False(afterPhone.Value.Complete);
        Assert.Equal(ModuleStepKind.DateTimePicker, afterPhone.Value.Step!.Kind);

        // The lost attempt still reached the store - losing is something only the write can decide -
        // and the visitor was handed a fresh choice rather than an error.
        Assert.Single(world.Bookings.Attempts);

        // A second attempt against the freshly re-offered slot can still succeed: the task is really
        // back at AwaitingSlotChoice, not stuck.
        var slotAction = Assert.Single(afterPhone.Value.Step!.Actions);
        world.Bookings.SlotIsClaimable = true;
        var retryPhone = await world.ReplyAsync(externalTaskId, ModuleStepKinds.DateTimePicker, slotAction.Value);
        var confirmed = await world.ReplyAsync(externalTaskId, ModuleStepKinds.Form, "+79990000002");
        Assert.True(confirmed.Value.Complete);
    }

    [Fact]
    public async Task AReplyWithTheWrongKind_IsRejectedBeforeTheValueIsInterpreted()
    {
        var world = new World();
        var start = await world.StartAsync();

        var result = await world.ReplyAsync(
            start.Value.ExternalTaskId, ModuleStepKinds.Form, BookingFixtures.ServiceId.Value.ToString());

        Assert.False(result.IsSuccess);
        Assert.Equal("chat_module_task.kind_mismatch", result.Error!.Value.Code);
    }

    [Fact]
    public async Task AReplyForAnUnknownTask_IsNotFound()
    {
        var world = new World();

        var result = await world.ReplyAsync(Guid.NewGuid().ToString(), ModuleStepKinds.ChoiceList, "anything");

        Assert.False(result.IsSuccess);
        Assert.Equal("chat_module_task.not_found", result.Error!.Value.Code);
    }

    [Fact]
    public async Task AMalformedExternalTaskId_IsNotFoundRatherThanAnException()
    {
        var world = new World();

        var result = await world.ReplyAsync("not-a-guid", ModuleStepKinds.ChoiceList, "anything");

        Assert.False(result.IsSuccess);
        Assert.Equal("chat_module_task.not_found", result.Error!.Value.Code);
    }

    [Fact]
    public async Task AReplyAfterTheTaskAlreadyCompleted_IsRejected()
    {
        var world = new World();
        var start = await world.StartAsync();
        var externalTaskId = start.Value.ExternalTaskId;
        await world.ReplyAsync(
            externalTaskId, ModuleStepKinds.ChoiceList, BookingFixtures.ServiceId.Value.ToString());
        await world.ReplyAsync(
            externalTaskId, ModuleStepKinds.ChoiceList, BookingFixtures.WorkerId.Value.ToString());
        await world.ReplyAsync(
            externalTaskId, ModuleStepKinds.DateTimePicker, BookingFixtures.EventId.Value.ToString());
        await world.ReplyAsync(externalTaskId, ModuleStepKinds.Form, "+79990000003");

        var afterCompletion = await world.ReplyAsync(externalTaskId, ModuleStepKinds.Form, "+79990000003");

        Assert.False(afterCompletion.IsSuccess);
        Assert.Equal("chat_module_task.already_complete", afterCompletion.Error!.Value.Code);
    }

    [Fact]
    public async Task AMalformedReplyValue_IsRejectedRatherThanReachingTheDownstreamHandler()
    {
        var world = new World();
        var start = await world.StartAsync();

        var result = await world.ReplyAsync(
            start.Value.ExternalTaskId, ModuleStepKinds.ChoiceList, "not-a-guid");

        Assert.False(result.IsSuccess);
        Assert.Equal("chat_module_task.invalid_reply_value", result.Error!.Value.Code);
        Assert.Empty(world.Bookings.Attempts);
    }

    /// <summary>The handlers plus their fakes, assembled once - the same shape
    /// <c>BookEventHandlerTests</c>' own <c>World</c> uses, extended with the two new handlers under
    /// test wired to real (non-faked) instances of the four existing handlers they reuse.</summary>
    private sealed class World
    {
        private readonly StartModuleTaskHandler _startHandler;
        private readonly ReplyToModuleTaskHandler _replyHandler;

        public World(Guid? configuredCalendarId = null)
        {
            var tenant = BookingFixtures.Tenant();
            var calendar = BookingFixtures.Calendar();
            var service = BookingFixtures.HaircutService();
            var worker = BookingFixtures.WorkerOffering(service);
            var slot = BookingFixtures.AvailableSlot();

            var tenantRepo = new FakeTenantRepository(tenant);
            var calendarRepo = new FakeCalendarRepository(calendar);
            var eventRepo = new FakeEventRepository(slot);
            var workerRepo = new FakeWorkerRepository(worker);
            var serviceRepo = new FakeServiceRepository(service);
            var clock = new FakeClock(BookingFixtures.Now);
            var idGenerator = new SequentialIdGenerator();

            ReadStore.Services.Add(new BookableServiceRow(BookingFixtures.ServiceId, "Haircut", 45));
            ReadStore.Workers.Add(new BookableWorkerRow(BookingFixtures.WorkerId, "Alex"));
            ReadStore.Slots.Add(new OpenSlotRow(
                BookingFixtures.EventId, BookingFixtures.WorkerId, "Alex",
                BookingFixtures.Slot.StartsAt, BookingFixtures.Slot.EndsAt, BookingFixtures.LocalDate));

            var resolver = new EmbedScopeResolver(tenantRepo, calendarRepo);
            var surfaceHandler = new GetBookingSurfaceHandler(resolver, calendarRepo, ReadStore);
            var workersHandler = new GetBookableWorkersHandler(resolver, ReadStore);
            var slotsHandler = new GetOpenSlotsHandler(resolver, ReadStore, clock);
            var bookHandler = new BookEventHandler(
                calendarRepo, tenantRepo, eventRepo, workerRepo, serviceRepo,
                Bookings, Limiter, new BookingRateLimitOptions(), new BookingOptions(), idGenerator, clock);

            var options = new ChatModuleTaskOptions
            {
                TenantPublicKey = "barbershop",
                CalendarId = configuredCalendarId ?? BookingFixtures.CalendarId.Value,
            };

            _startHandler = new StartModuleTaskHandler(surfaceHandler, tenantRepo, Tasks, options, idGenerator, clock);
            _replyHandler = new ReplyToModuleTaskHandler(
                Tasks, workersHandler, slotsHandler, ReadStore, bookHandler, options, clock);
        }

        public FakeBookingSurfaceReadStore ReadStore { get; } = new();

        public FakeChatBookingTaskStore Tasks { get; } = new();

        public FakeBookingStore Bookings { get; } = new();

        public FakeRateLimiter Limiter { get; } = new();

        public Task<Ago.Platform.Kernel.Result<ModuleTaskStarted>> StartAsync() =>
            _startHandler.HandleAsync(
                new StartModuleTask(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "/booking"),
                CancellationToken.None);

        public Task<Ago.Platform.Kernel.Result<ModuleTaskReplied>> ReplyAsync(
            string externalTaskId, string kind, string value) =>
            _replyHandler.HandleAsync(
                new ReplyToModuleTask(externalTaskId, Guid.NewGuid(), kind, value), CancellationToken.None);
    }
}
