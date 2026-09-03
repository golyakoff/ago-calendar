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

    /// <summary>`22-04`: a site with no provisioned tenant at all - the "module not enabled for this
    /// site" case, refused rather than falling back to the fixture's own tenant.</summary>
    [Fact]
    public async Task Start_WhenTheSiteIdMatchesNoProvisionedTenant_IsNotConfigured()
    {
        var world = new World();

        var result = await world.StartAsync(siteId: Guid.NewGuid());

        Assert.False(result.IsSuccess);
        Assert.Equal("chat_module_task.not_configured", result.Error!.Value.Code);
    }

    // `22-04`'s own Done-when ("two sites reach two different tenants") is proven at the HTTP level
    // instead of here - Ago.Calendar.Integration.Tests.ChatModuleTaskEndpointTests, over two real
    // per-tenant registrations and two real signed credentials. This fixture's Calendar/Service/Worker
    // helpers all hardcode BookingFixtures.TenantId, so a second, independently resolving tenant
    // cannot be expressed at this level without duplicating every one of those fixtures - the
    // integration suite is the level that already has a real database to seed a second tenant into.

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
        // `20-09`: the phone step's kind is now VerifiedPhoneForm, not plain Form - the signal Chat
        // reacts to before ever sending a reply here (RouteConversationToModuleHandler's own remarks).
        // The wire payload shape is unchanged (still a prompt/fieldId/fieldLabel form).
        Assert.Equal(ModuleStepKind.VerifiedPhoneForm, afterSlot.Value.Step!.Kind);
        Assert.Equal("phone", afterSlot.Value.Step!.FieldId);

        var afterPhone = await world.ReplyAsync(
            externalTaskId, ModuleStepKinds.VerifiedPhoneForm, "+79990000001", phoneVerifiedAt: BookingFixtures.Now);
        Assert.True(afterPhone.IsSuccess);
        Assert.True(afterPhone.Value.Complete);
        Assert.Equal(ModuleStepKind.ConfirmationCard, afterPhone.Value.Step!.Kind);

        // The booking write actually happened - the fake records it exactly like
        // BookEventHandlerTests asserts against it.
        var attempt = Assert.Single(world.Bookings.Attempts);
        Assert.Equal([BookingFixtures.EventId], attempt.EventIds);
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

        var afterPhone = await world.ReplyAsync(
            externalTaskId, ModuleStepKinds.VerifiedPhoneForm, "+79990000002", phoneVerifiedAt: BookingFixtures.Now);

        Assert.True(afterPhone.IsSuccess);
        Assert.False(afterPhone.Value.Complete);
        Assert.Equal(ModuleStepKind.DateTimePicker, afterPhone.Value.Step!.Kind);

        // The lost attempt still reached the store - losing is something only the write can decide -
        // and the visitor was handed a fresh choice rather than an error. It carried a verification
        // assertion too: losing the availability race is not the same failure as never having verified.
        Assert.Equal(BookingFixtures.Now, Assert.Single(world.Bookings.Attempts).PhoneVerifiedAt);

        // A second attempt against the freshly re-offered slot can still succeed: the task is really
        // back at AwaitingSlotChoice, not stuck.
        var slotAction = Assert.Single(afterPhone.Value.Step!.Actions);
        world.Bookings.SlotIsClaimable = true;
        var retryPhone = await world.ReplyAsync(externalTaskId, ModuleStepKinds.DateTimePicker, slotAction.Value);
        var confirmed = await world.ReplyAsync(
            externalTaskId, ModuleStepKinds.VerifiedPhoneForm, "+79990000002", phoneVerifiedAt: BookingFixtures.Now);
        Assert.True(confirmed.Value.Complete);
    }

    /// <summary>
    /// `20-09`'s own defense-in-depth: in the real, chat-originated flow, `Ago.Chat.*`'s own
    /// `RouteConversationToModuleHandler` never forwards a phone-step reply without a verified
    /// `ChannelIdentity` behind it (proven, on the Chat side, in
    /// <c>Ago.Chat.Integration.Tests.ModuleTaskGatewayIntegrationTests</c> against a real HTTP round
    /// trip). This test proves the other half: if a reply carrying no assertion ever reached this
    /// product's real <see cref="ReplyToModuleTaskHandler"/> anyway (a bug upstream, or a future
    /// caller that is not Chat), <see cref="BookEventHandler"/>'s own refusal - before
    /// <see cref="Event.Claim"/>'s real SQL path (<see cref="FakeBookingStore"/> standing in for it
    /// here) is ever reached - stops it, and the visitor is re-offered a choice rather than seeing a
    /// dead end, the identical no-dead-end shape a lost availability race already gets.
    /// </summary>
    [Fact]
    public async Task APhoneReplyWithNoVerificationAssertion_NeverReachesTheStore_AndReOffersFreshSlots()
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

        // No phoneVerifiedAt at all - exactly what a caller that skipped Chat's own gate would send.
        var afterPhone = await world.ReplyAsync(externalTaskId, ModuleStepKinds.VerifiedPhoneForm, "+79990000009");

        Assert.True(afterPhone.IsSuccess);
        Assert.False(afterPhone.Value.Complete);
        Assert.Equal(ModuleStepKind.DateTimePicker, afterPhone.Value.Step!.Kind);

        // BookEventHandler refuses before ever calling IBookingStore.TryBookAsync - BookingAttempt's
        // own PhoneVerifiedAt is non-nullable by construction, so there is no way to reach the store
        // with an unverified one at all. Nothing was written - the same "a rejected booking never
        // reaches the store" data-minimisation property BookEventHandlerTests already proves for every
        // other rejection reason.
        Assert.Empty(world.Bookings.Attempts);
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

    /// <summary>`22-04`: closes the asymmetry adr/0094 named between this route and Calendar's own
    /// Start route - a credential proven for a different tenant is refused as if the task did not
    /// exist, the identical property <c>Ago.Faq.Application.Tests</c>' own sibling test proves for
    /// that product.</summary>
    [Fact]
    public async Task AReplyWithACredentialForAnotherTenant_IsNotFound_AsIfTheTaskDidNotExist()
    {
        var world = new World();
        var start = await world.StartAsync();

        var result = await world.ReplyAsync(
            start.Value.ExternalTaskId, ModuleStepKinds.ChoiceList, BookingFixtures.ServiceId.Value.ToString(),
            credentialSiteId: Guid.NewGuid());

        Assert.False(result.IsSuccess);
        Assert.Equal("chat_module_task.not_found", result.Error!.Value.Code);
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
        await world.ReplyAsync(
            externalTaskId, ModuleStepKinds.VerifiedPhoneForm, "+79990000003", phoneVerifiedAt: BookingFixtures.Now);

        var afterCompletion = await world.ReplyAsync(
            externalTaskId, ModuleStepKinds.VerifiedPhoneForm, "+79990000003", phoneVerifiedAt: BookingFixtures.Now);

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
        private readonly TenantId _tenantId;

        public World()
        {
            var tenant = BookingFixtures.Tenant();
            _tenantId = tenant.Id;
            var calendar = BookingFixtures.Calendar();
            var service = BookingFixtures.HaircutService();
            var worker = BookingFixtures.WorkerOffering(service);
            var slot = BookingFixtures.AvailableSlot();

            var tenantRepo = new FakeTenantRepository(tenant);
            var calendarRepo = new FakeCalendarRepository(calendar);
            var eventRepo = new FakeEventRepository(slot);
            var workerRepo = new FakeWorkerRepository(worker);
            var serviceRepo = new FakeServiceRepository(service);
            var scheduleRepo = new FakeWorkerScheduleRepository(BookingFixtures.Schedule());
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
                calendarRepo, tenantRepo, eventRepo, workerRepo, serviceRepo, scheduleRepo,
                Bookings, Limiter, new BookingRateLimitOptions(), new BookingOptions(),
                // `20-10`: the chat-originated flow always supplies PhoneVerifiedAt directly
                // (RouteConversationToModuleHandler's own `14-15` evidence), so
                // PhoneVerificationAssertionResolver's own short-circuit never touches either fake
                // below - see PhoneVerificationAssertionResolver.ResolveAsync's own remarks.
                new PhoneVerificationAssertionResolver(
                    new FakeCustomerRepository(), new FakePendingPhoneVerificationRepository()),
                idGenerator, clock);

            // `22-04`: no more ChatModuleTaskOptions/ModuleCallCredentialOptions - StartModuleTaskHandler
            // resolves the tenant from the site id it is handed directly, and ReplyToModuleTaskHandler
            // resolves the tenant's public key itself, from the task's own TenantId.
            _startHandler = new StartModuleTaskHandler(tenantRepo, calendarRepo, ReadStore, Tasks, idGenerator, clock);
            _replyHandler = new ReplyToModuleTaskHandler(
                Tasks, tenantRepo, workersHandler, slotsHandler, ReadStore, bookHandler, clock);
        }

        public FakeBookingSurfaceReadStore ReadStore { get; } = new();

        public FakeChatBookingTaskStore Tasks { get; } = new();

        public FakeBookingStore Bookings { get; } = new();

        public FakeRateLimiter Limiter { get; } = new();

        /// <summary>`22-04`: the site id this call claims - defaults to this world's own fixture
        /// tenant (the happy path every existing test in this file exercises); a caller passes a
        /// different value to prove resolution genuinely depends on it rather than on some other
        /// ambient state.</summary>
        public Task<Ago.Platform.Kernel.Result<ModuleTaskStarted>> StartAsync(Guid? siteId = null) =>
            _startHandler.HandleAsync(
                new StartModuleTask(Guid.NewGuid(), siteId ?? _tenantId.Value, Guid.NewGuid(), "/booking"),
                CancellationToken.None);

        /// <summary>`22-04`: <paramref name="credentialSiteId"/> defaults to this world's own tenant -
        /// the identical "credential proved this task's own tenant" happy path every existing test
        /// exercises - see <see cref="ReplyToModuleTask.CredentialSiteId"/>'s own remarks. A caller
        /// passes a different value to prove the cross-tenant refusal.</summary>
        public Task<Ago.Platform.Kernel.Result<ModuleTaskReplied>> ReplyAsync(
            string externalTaskId, string kind, string value, DateTimeOffset? phoneVerifiedAt = null,
            Guid? credentialSiteId = null) =>
            _replyHandler.HandleAsync(
                new ReplyToModuleTask(
                    externalTaskId, Guid.NewGuid(), kind, value, phoneVerifiedAt, credentialSiteId ?? _tenantId.Value),
                CancellationToken.None);
    }
}
