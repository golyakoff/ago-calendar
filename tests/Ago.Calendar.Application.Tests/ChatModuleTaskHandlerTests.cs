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

    // ------------------------------------------------------------------------------------------
    // `25-37`: locale - every ModuleStepFactory string renders in the tenant's configured
    // language, and English stays available for a tenant configured that way (every other test
    // in this file, none of which mentions locale at all).
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Start_WithRussianLocale_RendersTheServiceChoicePromptInRussian()
    {
        var world = new World();

        var result = await world.StartAsync(locale: "Ru");

        Assert.True(result.IsSuccess);
        Assert.Equal("Что вы хотите забронировать?", result.Value.Step.Prompt);
    }

    /// <summary>The full walkthrough's own Russian twin - every step, in order, rendered in the
    /// tenant's configured language, ending in a Russian confirmation card. Proves `25-37`'s own
    /// Done-when for "at least one tenant" across the whole flow, not just the first step.</summary>
    [Fact]
    public async Task AFullWalkthrough_WithRussianLocale_RendersEveryStepInRussian()
    {
        var world = new World();

        var start = await world.StartAsync(locale: "Ru");
        Assert.Equal("Что вы хотите забронировать?", start.Value.Step.Prompt);
        var externalTaskId = start.Value.ExternalTaskId;

        var afterService = await world.ReplyAsync(
            externalTaskId, ModuleStepKinds.ChoiceList, BookingFixtures.ServiceId.Value.ToString(), locale: "Ru");
        Assert.Equal("С кем вы хотите записаться?", afterService.Value.Step!.Prompt);
        var workerAction = Assert.Single(afterService.Value.Step!.Actions);

        var afterWorker = await world.ReplyAsync(
            externalTaskId, ModuleStepKinds.ChoiceList, workerAction.Value, locale: "Ru");
        Assert.Equal("Выберите время:", afterWorker.Value.Step!.Prompt);
        var slotAction = Assert.Single(afterWorker.Value.Step!.Actions);

        var afterSlot = await world.ReplyAsync(
            externalTaskId, ModuleStepKinds.DateTimePicker, slotAction.Value, locale: "Ru");
        Assert.Equal(ModuleStepKind.VerifiedPhoneForm, afterSlot.Value.Step!.Kind);
        Assert.Equal("Какой номер телефона лучше всего подходит, чтобы с вами связаться?", afterSlot.Value.Step!.Prompt);
        Assert.Equal("Номер телефона", afterSlot.Value.Step!.FieldLabel);

        var afterPhone = await world.ReplyAsync(
            externalTaskId, ModuleStepKinds.VerifiedPhoneForm, "+79990000010", phoneVerifiedAt: BookingFixtures.Now,
            locale: "Ru");
        Assert.True(afterPhone.Value.Complete);
        Assert.Equal(ModuleStepKind.ConfirmationCard, afterPhone.Value.Step!.Kind);
        Assert.Equal("Вы записаны!", afterPhone.Value.Step!.ConfirmationTitle);
        Assert.Collection(
            afterPhone.Value.Step!.ConfirmationLines!,
            l => Assert.Equal("Услуга", l.Label),
            l => Assert.Equal("С кем", l.Label),
            l => Assert.Equal("Когда", l.Label));
    }

    /// <summary>`25-37`'s currency instruction, restated as a test: the surrounding word localises
    /// ("от") but the currency code itself never does.</summary>
    [Fact]
    public async Task Start_WithRussianLocaleAndAFromPricedService_LocalizesTheWordNotTheCurrency()
    {
        var world = new World();
        world.ReadStore.Services.Clear();
        world.ReadStore.Services.Add(
            new BookableServiceRow(BookingFixtures.ServiceId, "Haircut", 45, PriceMinorUnits: 150000, PriceIsFrom: true));

        var result = await world.StartAsync(locale: "Ru");

        var action = Assert.Single(result.Value.Step.Actions);
        Assert.Contains("от 1500 RUB", action.Label, StringComparison.Ordinal);
        Assert.DoesNotContain("руб", action.Label, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------------------------------
    // `25-38`: a visitor who already gave a phone number earlier in the conversation sees the
    // verified-phone step's own prompt name that number and explain why it is being asked again -
    // RequiresVerifiedPhone stays true regardless (20-09's guarantee, unweakened).
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task HandleSlotChosen_WithAKnownPhone_PrefillsThePromptWithItAndExplainsWhy()
    {
        var world = new World();
        var start = await world.StartAsync();
        var externalTaskId = start.Value.ExternalTaskId;
        await world.ReplyAsync(externalTaskId, ModuleStepKinds.ChoiceList, BookingFixtures.ServiceId.Value.ToString());
        await world.ReplyAsync(externalTaskId, ModuleStepKinds.ChoiceList, BookingFixtures.WorkerId.Value.ToString());

        var afterSlot = await world.ReplyAsync(
            externalTaskId, ModuleStepKinds.DateTimePicker, BookingFixtures.EventId.Value.ToString(),
            knownPhone: "+79990000011");

        // Still the strict kind - 20-09's guarantee is unweakened by a known number alone.
        Assert.Equal(ModuleStepKind.VerifiedPhoneForm, afterSlot.Value.Step!.Kind);
        Assert.Contains("+79990000011", afterSlot.Value.Step!.Prompt, StringComparison.Ordinal);
        // Names why this confirms the number for the booking, distinct from contact info on file -
        // the backlog item's own subject ("no explanation").
        Assert.Contains("confirm", afterSlot.Value.Step!.Prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleSlotChosen_WithNoKnownPhone_UsesThePlainPromptWithNoNumberNamed()
    {
        var world = new World();
        var start = await world.StartAsync();
        var externalTaskId = start.Value.ExternalTaskId;
        await world.ReplyAsync(externalTaskId, ModuleStepKinds.ChoiceList, BookingFixtures.ServiceId.Value.ToString());
        await world.ReplyAsync(externalTaskId, ModuleStepKinds.ChoiceList, BookingFixtures.WorkerId.Value.ToString());

        var afterSlot = await world.ReplyAsync(
            externalTaskId, ModuleStepKinds.DateTimePicker, BookingFixtures.EventId.Value.ToString());

        Assert.Equal(ModuleStepKind.VerifiedPhoneForm, afterSlot.Value.Step!.Kind);
        Assert.Equal("What's the best phone number to reach you on?", afterSlot.Value.Step!.Prompt);
    }

    /// <summary>`25-38`'s own explicit guardrail: a known number changes only the wording and the
    /// prefill, never the requirement itself - a typed-back number still has to carry a real
    /// verification assertion or the booking is refused exactly as it always was.</summary>
    [Fact]
    public async Task HandleSlotChosen_WithAKnownPhone_StillRequiresAVerificationAssertionToBook()
    {
        var world = new World();
        var start = await world.StartAsync();
        var externalTaskId = start.Value.ExternalTaskId;
        await world.ReplyAsync(externalTaskId, ModuleStepKinds.ChoiceList, BookingFixtures.ServiceId.Value.ToString());
        await world.ReplyAsync(externalTaskId, ModuleStepKinds.ChoiceList, BookingFixtures.WorkerId.Value.ToString());
        await world.ReplyAsync(
            externalTaskId, ModuleStepKinds.DateTimePicker, BookingFixtures.EventId.Value.ToString(),
            knownPhone: "+79990000012");

        // No phoneVerifiedAt - exactly what a caller that skipped Chat's own verification gate would send.
        var afterPhone = await world.ReplyAsync(
            externalTaskId, ModuleStepKinds.VerifiedPhoneForm, "+79990000012", knownPhone: "+79990000012");

        Assert.False(afterPhone.Value.Complete);
        Assert.Equal(ModuleStepKind.DateTimePicker, afterPhone.Value.Step!.Kind);
        Assert.Empty(world.Bookings.Attempts);
    }

    // ------------------------------------------------------------------------------------------
    // `25-39`: a tenant's own AcceptUnverifiedPhone setting, off by default. On, with a
    // known phone: picking a slot completes the booking directly, no phone-form step shown at
    // all. On, with no known phone: the phone step still shows, but without the verification
    // requirement. Off: today's behaviour, entirely unchanged (every test above already proves
    // that, since none of them pass the flag).
    // ------------------------------------------------------------------------------------------

    /// <summary>The Done-when's own words: "picking a slot completes the booking directly - no
    /// phone-form step is shown at all, proven by a test asserting the reply to
    /// HandleSlotChosenAsync is a completion/confirmation step, not PhoneForm."</summary>
    [Fact]
    public async Task HandleSlotChosen_WithSettingOnAndAKnownPhone_CompletesTheBookingDirectly_NoPhoneStepAtAll()
    {
        var world = new World();
        var start = await world.StartAsync();
        var externalTaskId = start.Value.ExternalTaskId;
        await world.ReplyAsync(externalTaskId, ModuleStepKinds.ChoiceList, BookingFixtures.ServiceId.Value.ToString());
        await world.ReplyAsync(externalTaskId, ModuleStepKinds.ChoiceList, BookingFixtures.WorkerId.Value.ToString());

        var afterSlot = await world.ReplyAsync(
            externalTaskId, ModuleStepKinds.DateTimePicker, BookingFixtures.EventId.Value.ToString(),
            knownPhone: "+79990000013", acceptUnverifiedPhone: true);

        Assert.True(afterSlot.IsSuccess);
        Assert.True(afterSlot.Value.Complete);
        Assert.Equal(ModuleStepKind.ConfirmationCard, afterSlot.Value.Step!.Kind);

        // The booking write actually happened, with the known number and no verification instant -
        // the record must say the phone was never verified, not silently treated as equivalent to a
        // verified one (this item's own Done-when).
        var attempt = Assert.Single(world.Bookings.Attempts);
        Assert.Equal("+79990000013", attempt.Phone.Value);
        Assert.Null(attempt.PhoneVerifiedAt);
    }

    /// <summary>The Done-when's own second case, named explicitly as the fallback rather than the
    /// common path: no phone known yet, so the step still has to appear - but without the
    /// verification requirement.</summary>
    [Fact]
    public async Task HandleSlotChosen_WithSettingOnAndNoKnownPhone_StillShowsThePhoneStep_ButWithoutVerification()
    {
        var world = new World();
        var start = await world.StartAsync();
        var externalTaskId = start.Value.ExternalTaskId;
        await world.ReplyAsync(externalTaskId, ModuleStepKinds.ChoiceList, BookingFixtures.ServiceId.Value.ToString());
        await world.ReplyAsync(externalTaskId, ModuleStepKinds.ChoiceList, BookingFixtures.WorkerId.Value.ToString());

        var afterSlot = await world.ReplyAsync(
            externalTaskId, ModuleStepKinds.DateTimePicker, BookingFixtures.EventId.Value.ToString(),
            acceptUnverifiedPhone: true);

        Assert.True(afterSlot.IsSuccess);
        Assert.False(afterSlot.Value.Complete);
        // Plain Form, not VerifiedPhoneForm - the kind that actually turns Chat's own verification
        // gate off, not merely a hint (ModuleStepFactory.PhoneForm's own remarks).
        Assert.Equal(ModuleStepKind.Form, afterSlot.Value.Step!.Kind);

        // Completing from here needs no verification assertion at all - a typed number with no
        // phoneVerifiedAt still books, unlike every VerifiedPhoneForm-kind test in this file.
        var afterPhone = await world.ReplyAsync(
            externalTaskId, ModuleStepKinds.Form, "+79990000014", acceptUnverifiedPhone: true);

        Assert.True(afterPhone.Value.Complete);
        Assert.Equal(ModuleStepKind.ConfirmationCard, afterPhone.Value.Step!.Kind);
        var attempt = Assert.Single(world.Bookings.Attempts);
        Assert.Equal("+79990000014", attempt.Phone.Value);
        Assert.Null(attempt.PhoneVerifiedAt);
    }

    /// <summary>The Done-when's own third case: off (the default), both of the above are unchanged
    /// from today's behaviour - proven directly rather than only inferred from every other test in
    /// this file never passing the flag.</summary>
    [Fact]
    public async Task HandleSlotChosen_WithTheSettingOff_BehavesExactlyAsBeforeEvenWithAKnownPhone()
    {
        var world = new World();
        var start = await world.StartAsync();
        var externalTaskId = start.Value.ExternalTaskId;
        await world.ReplyAsync(externalTaskId, ModuleStepKinds.ChoiceList, BookingFixtures.ServiceId.Value.ToString());
        await world.ReplyAsync(externalTaskId, ModuleStepKinds.ChoiceList, BookingFixtures.WorkerId.Value.ToString());

        var afterSlot = await world.ReplyAsync(
            externalTaskId, ModuleStepKinds.DateTimePicker, BookingFixtures.EventId.Value.ToString(),
            knownPhone: "+79990000015", acceptUnverifiedPhone: false);

        Assert.False(afterSlot.Value.Complete);
        Assert.Equal(ModuleStepKind.VerifiedPhoneForm, afterSlot.Value.Step!.Kind);
        Assert.Empty(world.Bookings.Attempts);
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

    /// <summary>
    /// `25-32`: the live bug, reproduced at this level rather than only inferred. `Ago.Chat.*`'s own
    /// <c>ModuleResiliencePipelines</c> retries any exception on a reply call, including a timeout on a
    /// request Calendar already committed - so the *same* service-choice reply can legitimately reach
    /// this handler twice, once while the task is still <c>AwaitingServiceChoice</c> and once after it
    /// has already advanced to <c>AwaitingWorkerChoice</c>. Both states read the identical wire kind
    /// (<c>choice_list</c>), so before this item's fix the second call sailed past
    /// <c>KindMatches</c> and was misread as an answer to the *worker*-choice step: the service id got
    /// parsed as a worker id and handed to <c>GetOpenSlotsHandler</c>, which found no such worker and
    /// answered with a real <c>date_time_picker</c> step carrying zero slots - the exact
    /// `{"prompt":"Pick a time:","slots":[]}` this item's own live evidence shows, with the
    /// "Who would you like to book with?" step never shown at all. The fixture's calendar has exactly
    /// one worker, matching the live conversation this item names.
    /// </summary>
    [Fact]
    public async Task ARetriedServiceChoiceReply_ReplaysTheWorkerChoiceStep_RatherThanCorruptingIt()
    {
        var world = new World();
        var start = await world.StartAsync();
        var externalTaskId = start.Value.ExternalTaskId;

        var firstDelivery = await world.ReplyAsync(
            externalTaskId, ModuleStepKinds.ChoiceList, BookingFixtures.ServiceId.Value.ToString());
        Assert.True(firstDelivery.IsSuccess);
        Assert.Equal(ModuleStepKind.ChoiceList, firstDelivery.Value.Step!.Kind);
        var firstWorkerAction = Assert.Single(firstDelivery.Value.Step!.Actions);
        Assert.Equal(BookingFixtures.WorkerId.Value.ToString(), firstWorkerAction.Value);

        // The retry: byte-identical request, arriving after the first one already committed.
        var retried = await world.ReplyAsync(
            externalTaskId, ModuleStepKinds.ChoiceList, BookingFixtures.ServiceId.Value.ToString());

        Assert.True(retried.IsSuccess);
        Assert.False(retried.Value.Complete);
        // The bug: this used to come back ModuleStepKind.DateTimePicker with an empty slots list.
        Assert.Equal(ModuleStepKind.ChoiceList, retried.Value.Step!.Kind);
        var retriedWorkerAction = Assert.Single(retried.Value.Step!.Actions);
        Assert.Equal(BookingFixtures.WorkerId.Value.ToString(), retriedWorkerAction.Value);

        // The flow is still genuinely usable afterwards - the replay did not leave the task stuck or
        // double-advanced.
        var afterWorker = await world.ReplyAsync(externalTaskId, ModuleStepKinds.ChoiceList, retriedWorkerAction.Value);
        Assert.True(afterWorker.IsSuccess);
        Assert.Equal(ModuleStepKind.DateTimePicker, afterWorker.Value.Step!.Kind);
        var slotAction = Assert.Single(afterWorker.Value.Step!.Actions);
        Assert.Equal(BookingFixtures.EventId.Value.ToString(), slotAction.Value);
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
        public Task<Ago.Platform.Kernel.Result<ModuleTaskStarted>> StartAsync(Guid? siteId = null, string locale = "En") =>
            _startHandler.HandleAsync(
                new StartModuleTask(Guid.NewGuid(), siteId ?? _tenantId.Value, Guid.NewGuid(), "/booking", locale),
                CancellationToken.None);

        /// <summary>`22-04`: <paramref name="credentialSiteId"/> defaults to this world's own tenant -
        /// the identical "credential proved this task's own tenant" happy path every existing test
        /// exercises - see <see cref="ReplyToModuleTask.CredentialSiteId"/>'s own remarks. A caller
        /// passes a different value to prove the cross-tenant refusal. `25-37`/`25-38`/`25-39`:
        /// <paramref name="locale"/>/<paramref name="knownPhone"/>/<paramref name="acceptUnverifiedPhone"/>
        /// default to English/none-known/off - today's behaviour for every existing test in this file
        /// that never mentions them.</summary>
        public Task<Ago.Platform.Kernel.Result<ModuleTaskReplied>> ReplyAsync(
            string externalTaskId, string kind, string value, DateTimeOffset? phoneVerifiedAt = null,
            Guid? credentialSiteId = null, string locale = "En", string? knownPhone = null,
            bool acceptUnverifiedPhone = false) =>
            _replyHandler.HandleAsync(
                new ReplyToModuleTask(
                    externalTaskId, Guid.NewGuid(), kind, value, phoneVerifiedAt, credentialSiteId ?? _tenantId.Value,
                    locale, knownPhone, acceptUnverifiedPhone),
                CancellationToken.None);
    }
}
