using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ago.Calendar.Contracts;
using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres;
using Microsoft.EntityFrameworkCore;

namespace Ago.Calendar.Integration.Tests;

/// <summary>
/// `20-07`'s chat-module wire contract over real HTTP, against the real <c>Ago.Calendar.Api</c> host,
/// a real Postgres and a real Redis.
///
/// <para><b>Why this exists on top of the handler tests.</b> The same reason
/// <c>BookingEndpointTests</c> exists on top of <c>BookEventHandlerTests</c>: a full walk through all
/// five steps proving a real booked <see cref="Event"/> row is a claim about what the whole stack does
/// together - <c>ChatModuleTaskEndpoints</c>' mapping to <see cref="StepDto"/>, the module's DI wiring,
/// and the real <c>ChatBookingTaskStore</c>/EF round trip - none of which the fake-backed handler tests
/// exercise.</para>
///
/// <para><b>`22-04`: no more <c>ChatModule:TenantPublicKey</c>/<c>ChatModule:CalendarId</c>/
/// <c>ChatModule:SharedSecret</c> settings.</b> This suite now seeds a real
/// <see cref="ChatModuleRegistration"/> row per tenant through <see cref="RegisterChatModuleAsync"/> -
/// the registry's own consuming half, proven separately for its domain shape in
/// <c>Ago.Calendar.Domain.Tests</c> - and every call's own <c>siteId</c> is that tenant's real
/// <see cref="TenantId"/>, matching how a real deployment resolves once `22-04` ships (no console or
/// provisioning endpoint exists yet to do this over HTTP - out of this item's own scope, see this
/// item's report).</para>
///
/// <para><b>The wire's exact casing is asserted against the real serialized bytes</b>, not against a
/// hand-rolled <c>JsonSerializerOptions</c> copy that could quietly drift from what
/// <c>Ago.Calendar.Api</c> actually ships - the same reasoning <c>BookingEndpointTests</c> already
/// applies to its own "no 'pending' anywhere in this payload" assertion.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public class ChatModuleTaskEndpointTests(PostgresFixture fixture) : IAsyncLifetime
{
    private CalendarApiFactory _factory = null!;
    private HttpClient _client = null!;
    private SeededTenant _seed = null!;

    /// <summary>`25-45`: the single anchor `InitializeAsync`'s own first slot and
    /// `AFailedBookingAttempt_ReOffersFreshSlots_AndTheSecondAttemptCanStillSucceed`'s own second slot
    /// both build off, instead of each calling `DateTimeOffset.UtcNow.AddDays(7)` independently. See
    /// that field's own remarks, set in `InitializeAsync`, for why two independent reads of the real
    /// clock's own uncontrolled time-of-day was the actual, confirmed cause of `25-45`'s own flake.
    /// </summary>
    private DateTimeOffset _seedSlotsAnchor;

    /// <summary>The secret registered for <see cref="_seed"/>'s own tenant - see
    /// <see cref="RegisterChatModuleAsync"/>.</summary>
    private const string TestSharedSecret = "integration-test-shared-secret-of-sufficient-length";

    public async Task InitializeAsync()
    {
        _seed = await CalendarSeed.WriteAsync(fixture, publicKey: $"chatmod-{CalendarSeed.NewId():N}"[..24]);

        // `20-18`: BookEventHandler now run-finds through the worker's own schedule.
        await CalendarSeed.AddWeeklyScheduleAsync(fixture, _seed, horizonDays: 30);

        // Slots are inserted directly rather than through the materialiser (`20-02`) - the same
        // shortcut BookingEndpointTests' own BookableSlotsAsync takes, since this suite is about the
        // chat-module wire contract, not availability generation.
        //
        // Far enough ahead that "starts_at > now" holds against the host's own real clock - this
        // test cannot inject a fake one, exactly like BookingEndpointTests' own slots.
        //
        // `25-45`: the date component alone comes from the real clock (so this stays genuinely in
        // the future no matter which real date the suite runs on); the time-of-day is a fixed 09:00
        // UTC, not whatever real hour `DateTimeOffset.UtcNow` happens to return. CalendarSeed.Slot's
        // own LocalDate is the UTC date component of its own startsAt, not a real conversion through
        // the tenant's own timezone (the shortcut every directly-inserted slot in this project takes) -
        // so a second slot built by adding a couple of hours to this anchor (the re-offer test's own
        // secondSlot) can never roll into the next UTC date the way it could when both slots read
        // DateTimeOffset.UtcNow independently: that read whatever hour the real clock happened to be
        // at, and for roughly two hours out of every UTC day (22:00-24:00), a +2h offset crossed
        // midnight and split one intended date into two - `25-45`'s own confirmed cause, reproduced by
        // checking the real UTC hour at the moment the suite ran, not a guess.
        _seedSlotsAnchor = new DateTimeOffset(DateTimeOffset.UtcNow.AddDays(7).Date, TimeSpan.Zero).AddHours(9);
        var slot = CalendarSeed.Slot(_seed, _seedSlotsAnchor);
        await using (var db = fixture.CreateDbContext())
        {
            await new EventRepository(db).AddRangeAsync([slot], CancellationToken.None);
        }

        await RegisterChatModuleAsync(_seed.Tenant.Id, TestSharedSecret);

        _factory = new CalendarApiFactory(fixture);
        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task AFullWalkthrough_AllFiveSteps_EndsInACompleteConfirmation_WithARealBookedEvent()
    {
        var startResponse = await StartAsync(Guid.NewGuid(), _seed.Tenant.Id.Value, Guid.NewGuid(), "/booking");
        Assert.Equal(HttpStatusCode.OK, startResponse.StatusCode);

        var started = await startResponse.Content.ReadFromJsonAsync<ModuleTaskStartResponse>();
        Assert.False(started!.Complete);
        Assert.Equal(ModuleStepKinds.ChoiceList, started.Step.Kind);
        var serviceAction = Assert.Single(started.Step.Actions);
        Assert.Equal(_seed.Service.Id.Value.ToString(), serviceAction.Value);
        // `23-35`: the seeded service carries no price (CalendarSeed.WriteAsync's own default), so
        // ModuleStepFactory.DescribeService's price branch is not exercised here - proving instead
        // that its no-price rendering is unchanged, the one assertion this walkthrough can make about
        // it without a second, price-carrying seed.
        Assert.Equal("Haircut (45 min)", serviceAction.Label);

        var afterService = await ReplyAsync(
            started.ExternalTaskId, ModuleStepKinds.ChoiceList, serviceAction.Value);
        Assert.Equal(ModuleStepKinds.ChoiceList, afterService.Step!.Kind);
        var workerAction = Assert.Single(afterService.Step.Actions);
        Assert.Equal(_seed.Worker.Id.Value.ToString(), workerAction.Value);

        var afterWorker = await ReplyAsync(started.ExternalTaskId, ModuleStepKinds.ChoiceList, workerAction.Value);
        // `25-33`: the date round, not the flat slot list directly - one seeded day, one action.
        Assert.Equal(ModuleStepKinds.DateTimePicker, afterWorker.Step!.Kind);
        var dateAction = Assert.Single(afterWorker.Step.Actions);

        var afterDate = await ReplyAsync(started.ExternalTaskId, ModuleStepKinds.DateTimePicker, dateAction.Value);
        Assert.Equal(ModuleStepKinds.DateTimePicker, afterDate.Step!.Kind);
        var slotAction = Assert.Single(afterDate.Step.Actions);

        var afterSlot = await ReplyAsync(started.ExternalTaskId, ModuleStepKinds.DateTimePicker, slotAction.Value);
        // `20-09`: the phone step's own kind, signalling to Chat that the reply must carry a verified
        // phone - see ModuleStepFactory.PhoneForm's own remarks. Wire payload shape is unchanged.
        Assert.Equal(ModuleStepKinds.VerifiedPhoneForm, afterSlot.Step!.Kind);

        var afterPhone = await ReplyAsync(
            started.ExternalTaskId, ModuleStepKinds.VerifiedPhoneForm, "+79997000001", phoneVerifiedAt: DateTimeOffset.UtcNow);
        Assert.True(afterPhone.Complete);
        Assert.Equal(ModuleStepKinds.ConfirmationCard, afterPhone.Step!.Kind);

        // The real row, not a fake's own record of an attempt.
        await using var db = fixture.CreateDbContext();
        var bookedEventId = new EventId(Guid.Parse(slotAction.Value));
        var stored = await db.Events.SingleAsync(e => e.Id == bookedEventId);
        Assert.Equal(EventStatus.PendingConfirmation, stored.Status);
        Assert.NotNull(stored.CustomerId);
    }

    /// <summary>`25-37`: the same walkthrough over real HTTP, in Russian - proving the locale reaches
    /// this product over the real wire (<see cref="ModuleTaskStartRequest.Locale"/>), not only through
    /// the fake-backed <c>Ago.Calendar.Application.Tests</c> proof.</summary>
    [Fact]
    public async Task AFullWalkthrough_WithRussianLocaleOverRealHttp_RendersEveryStepInRussian()
    {
        var startResponse = await StartAsync(
            Guid.NewGuid(), _seed.Tenant.Id.Value, Guid.NewGuid(), "/booking", locale: "Ru");
        var started = await startResponse.Content.ReadFromJsonAsync<ModuleTaskStartResponse>();
        Assert.Equal("Что вы хотите забронировать?", Prompt(started!.Step));
        var serviceAction = Assert.Single(started.Step.Actions);

        var afterService = await ReplyAsync(
            started.ExternalTaskId, ModuleStepKinds.ChoiceList, serviceAction.Value, locale: "Ru");
        Assert.Equal("С кем вы хотите записаться?", Prompt(afterService.Step!));
        var workerAction = Assert.Single(afterService.Step!.Actions);

        var afterWorker = await ReplyAsync(
            started.ExternalTaskId, ModuleStepKinds.ChoiceList, workerAction.Value, locale: "Ru");
        Assert.Equal(ModuleStepKinds.DateTimePicker, afterWorker.Step!.Kind);
        Assert.Equal("Выберите дату:", Prompt(afterWorker.Step));
        var dateAction = Assert.Single(afterWorker.Step.Actions);

        var afterDate = await ReplyAsync(
            started.ExternalTaskId, ModuleStepKinds.DateTimePicker, dateAction.Value, locale: "Ru");
        Assert.Equal(ModuleStepKinds.DateTimePicker, afterDate.Step!.Kind);
        Assert.Contains("Выберите время на", Prompt(afterDate.Step), StringComparison.Ordinal);
        var slotAction = Assert.Single(afterDate.Step.Actions);

        var afterSlot = await ReplyAsync(
            started.ExternalTaskId, ModuleStepKinds.DateTimePicker, slotAction.Value, locale: "Ru");
        Assert.Equal(ModuleStepKinds.VerifiedPhoneForm, afterSlot.Step!.Kind);
        Assert.Equal(
            "Какой номер телефона лучше всего подходит, чтобы с вами связаться?", Prompt(afterSlot.Step));
    }

    /// <summary>`25-39`'s own Done-when, over real HTTP and a real Postgres row rather than only the
    /// fake-backed proof: a tenant's own <c>AcceptUnverifiedPhone</c> flag, on, with a phone already
    /// known from the conversation, completes the booking the moment a slot is chosen - no phone-form
    /// step reaches the wire at all.</summary>
    [Fact]
    public async Task ASlotChoiceReply_WithTheSettingOnAndAKnownPhone_CompletesOverRealHttp_WithNoPhoneStepOnTheWire()
    {
        var startResponse = await StartAsync(Guid.NewGuid(), _seed.Tenant.Id.Value, Guid.NewGuid(), "/booking");
        var started = await startResponse.Content.ReadFromJsonAsync<ModuleTaskStartResponse>();
        var serviceAction = Assert.Single(started!.Step.Actions);

        var afterService = await ReplyAsync(started.ExternalTaskId, ModuleStepKinds.ChoiceList, serviceAction.Value);
        var workerAction = Assert.Single(afterService.Step!.Actions);

        var afterWorker = await ReplyAsync(started.ExternalTaskId, ModuleStepKinds.ChoiceList, workerAction.Value);
        var dateAction = Assert.Single(afterWorker.Step!.Actions);

        var afterDate = await ReplyAsync(started.ExternalTaskId, ModuleStepKinds.DateTimePicker, dateAction.Value);
        var slotAction = Assert.Single(afterDate.Step!.Actions);

        var afterSlot = await ReplyAsync(
            started.ExternalTaskId, ModuleStepKinds.DateTimePicker, slotAction.Value,
            knownPhone: "+79997000099", acceptUnverifiedPhone: true);

        Assert.True(afterSlot.Complete);
        Assert.Equal(ModuleStepKinds.ConfirmationCard, afterSlot.Step!.Kind);

        await using var db = fixture.CreateDbContext();
        var bookedEventId = new EventId(Guid.Parse(slotAction.Value));
        var stored = await db.Events.SingleAsync(e => e.Id == bookedEventId);
        Assert.NotNull(stored.CustomerId);
        var customer = await db.Customers.SingleAsync(c => c.Id == stored.CustomerId!.Value);
        Assert.Equal("+79997000099", customer.Phone.Value);
        // The record says the phone was never verified - not silently equivalent to a verified one.
        Assert.Null(customer.PhoneVerifiedAt);
    }

    /// <summary>
    /// `25-32`: the live bug, reproduced over a real HTTP round trip and a real Postgres row rather
    /// than only the fake-backed proof in <c>Ago.Calendar.Application.Tests</c> - the level this item's
    /// own Done-when asks for ("not only a fresh test fixture"). This suite's default seed is already
    /// shaped like the live conversation this item names: one tenant, one calendar, exactly one worker,
    /// real future <c>Available</c> events. `Ago.Chat.*`'s own <c>ModuleResiliencePipelines</c> retries
    /// any exception on a reply call - including a timeout on a request Calendar already committed - so
    /// the identical service-choice reply can legitimately reach this endpoint twice. Before this item's
    /// fix, the second call landed on the just-advanced <c>AwaitingWorkerChoice</c> state (which reads
    /// the same wire kind as <c>AwaitingServiceChoice</c>) and was misread as a worker id, producing a
    /// real <c>date_time_picker</c> response with zero slots while "Who would you like to book with?"
    /// was never sent at all.
    /// </summary>
    [Fact]
    public async Task ARetriedServiceChoiceReply_OverRealHttpAndPostgres_ReplaysTheWorkerChoiceStep()
    {
        var started = (await (await StartAsync(Guid.NewGuid(), _seed.Tenant.Id.Value, Guid.NewGuid(), "/booking"))
            .Content.ReadFromJsonAsync<ModuleTaskStartResponse>())!;

        var firstDelivery = await ReplyAsync(
            started.ExternalTaskId, ModuleStepKinds.ChoiceList, _seed.Service.Id.Value.ToString());
        Assert.Equal(ModuleStepKinds.ChoiceList, firstDelivery.Step!.Kind);
        var firstWorkerAction = Assert.Single(firstDelivery.Step.Actions);
        Assert.Equal(_seed.Worker.Id.Value.ToString(), firstWorkerAction.Value);

        // The retry: byte-identical request, arriving after the database already committed the first
        // one's advance to AwaitingWorkerChoice - a real row read back over a real connection, not a
        // fake's in-memory dictionary.
        var retried = await ReplyAsync(
            started.ExternalTaskId, ModuleStepKinds.ChoiceList, _seed.Service.Id.Value.ToString());
        Assert.Equal(ModuleStepKinds.ChoiceList, retried.Step!.Kind);
        var retriedWorkerAction = Assert.Single(retried.Step.Actions);
        Assert.Equal(_seed.Worker.Id.Value.ToString(), retriedWorkerAction.Value);

        // Still genuinely usable afterwards: the real slot this suite seeded in InitializeAsync comes
        // back, not an empty list.
        var afterWorker = await ReplyAsync(started.ExternalTaskId, ModuleStepKinds.ChoiceList, retriedWorkerAction.Value);
        Assert.Equal(ModuleStepKinds.DateTimePicker, afterWorker.Step!.Kind);
        Assert.NotEmpty(afterWorker.Step.Actions);
    }

    /// <summary>`25-33`'s own Done-when, over a real HTTP round trip and a real Postgres row rather
    /// than only the fake-backed proof in <c>Ago.Calendar.Application.Tests</c>: a second day's own
    /// slot, seeded on top of this suite's default single-day slot, produces a date round with two
    /// dates, and choosing one shows only that date's own time - never the other day's slot leaking
    /// into it. Two independent tasks, not one exploring both dates: choosing a date commits that
    /// task to it (<see cref="Domain.ChatBookingTask.ChooseDate"/>'s own <c>RequireState</c> guard),
    /// so proving both dates' own time rounds needs two separate walkthroughs, each choosing a
    /// different one of the two dates this calendar actually offers.</summary>
    [Fact]
    public async Task ASecondDaysSlot_ProducesTwoDateChoices_AndEachOwnTimeRoundStaysScopedToItsOwnDate()
    {
        var secondDaySlot = CalendarSeed.Slot(_seed, DateTimeOffset.UtcNow.AddDays(8));
        await using (var db = fixture.CreateDbContext())
        {
            await new EventRepository(db).AddRangeAsync([secondDaySlot], CancellationToken.None);
        }

        var (probeTaskId, dateActions) = await StartToDateRoundAsync();
        Assert.Equal(2, dateActions.Count);
        // Chronological order, not insertion order - the earlier-seeded (InitializeAsync's own,
        // day 7) date first.
        var earlierDate = dateActions[0];
        var laterDate = dateActions[1];
        Assert.NotEqual(earlierDate.Value, laterDate.Value);

        var afterEarlierDate = await ReplyAsync(probeTaskId, ModuleStepKinds.DateTimePicker, earlierDate.Value);
        var earlierTimeAction = Assert.Single(afterEarlierDate.Step!.Actions);

        // A second, independent task for the second date - the probe task above already committed
        // to the earlier one (ChooseDate's own RequireState guard).
        var (secondTaskId, _) = await StartToDateRoundAsync();
        var afterLaterDate = await ReplyAsync(secondTaskId, ModuleStepKinds.DateTimePicker, laterDate.Value);
        var laterTimeAction = Assert.Single(afterLaterDate.Step!.Actions);

        // Each date's own time round names exactly one slot, and the two are not the same slot -
        // the second day's row never leaked into the first day's time round or vice versa.
        Assert.NotEqual(earlierTimeAction.Value, laterTimeAction.Value);
    }

    /// <summary>Walks a fresh task from <c>Start</c> through service and worker choice, returning
    /// both its id and the date round's own actions - the setup every date-round assertion in this
    /// suite's own `25-33` tests needs.</summary>
    private async Task<(string TaskId, IReadOnlyList<ModuleActionDto> DateActions)> StartToDateRoundAsync()
    {
        var started = (await (await StartAsync(Guid.NewGuid(), _seed.Tenant.Id.Value, Guid.NewGuid(), "/booking"))
            .Content.ReadFromJsonAsync<ModuleTaskStartResponse>())!;
        var afterService = await ReplyAsync(
            started.ExternalTaskId, ModuleStepKinds.ChoiceList, _seed.Service.Id.Value.ToString());
        var afterWorker = await ReplyAsync(
            started.ExternalTaskId, ModuleStepKinds.ChoiceList, Assert.Single(afterService.Step!.Actions).Value);

        Assert.Equal(ModuleStepKinds.DateTimePicker, afterWorker.Step!.Kind);
        return (started.ExternalTaskId, afterWorker.Step.Actions);
    }

    [Fact]
    public async Task AFailedBookingAttempt_ReOffersFreshSlots_AndTheSecondAttemptCanStillSucceed()
    {
        // A second slot for the same worker, so there is something left to re-offer once the first
        // is claimed out from under this task by somebody else. `25-45`: built off InitializeAsync's
        // own _seedSlotsAnchor, not a second independent DateTimeOffset.UtcNow read - see that
        // field's own remarks for why two independent reads were the actual, confirmed cause of this
        // test's own flake.
        var secondSlot = CalendarSeed.Slot(_seed, _seedSlotsAnchor.AddHours(2));
        await using (var db = fixture.CreateDbContext())
        {
            await new EventRepository(db).AddRangeAsync([secondSlot], CancellationToken.None);
        }

        var started = (await (await StartAsync(Guid.NewGuid(), _seed.Tenant.Id.Value, Guid.NewGuid(), "/booking"))
            .Content.ReadFromJsonAsync<ModuleTaskStartResponse>())!;

        var afterService = await ReplyAsync(
            started.ExternalTaskId, ModuleStepKinds.ChoiceList, _seed.Service.Id.Value.ToString());
        var afterWorker = await ReplyAsync(
            started.ExternalTaskId, ModuleStepKinds.ChoiceList, Assert.Single(afterService.Step!.Actions).Value);
        // `25-33`: both seeded slots fall on the same day (two hours apart), so the date round offers
        // exactly one date - the two slots themselves are the *time* round's own two actions.
        var dateAction = Assert.Single(afterWorker.Step!.Actions);
        var afterDate = await ReplyAsync(started.ExternalTaskId, ModuleStepKinds.DateTimePicker, dateAction.Value);
        var offeredSlotValues = afterDate.Step!.Actions.Select(a => a.Value).ToList();
        Assert.Equal(2, offeredSlotValues.Count);

        var afterSlot = await ReplyAsync(started.ExternalTaskId, ModuleStepKinds.DateTimePicker, offeredSlotValues[0]);
        Assert.Equal(ModuleStepKinds.VerifiedPhoneForm, afterSlot.Step!.Kind);

        // Somebody else takes that exact slot before the phone number arrives - a real race, not a
        // fake flag. `20-09`: claimed directly through the domain aggregate rather than the public
        // `/book` endpoint a widget would use, because that endpoint can no longer complete any
        // booking at all (BookingEndpointTests's own remarks) - this is the identical shortcut
        // ConfirmationSweepTests' own APendingBookingAsync already takes for the same reason (proving
        // a race outcome, not this item's own verification gate, which is what the phone-step replies
        // in this test are already exercising).
        await using (var db = fixture.CreateDbContext())
        {
            var stolenSlot = await new EventRepository(db).GetByIdAsync(
                new EventId(Guid.Parse(offeredSlotValues[0])), CancellationToken.None);
            var customer = Customer.Register(
                new CustomerId(CalendarSeed.NewId()), _seed.Tenant.Id, new PhoneNumber("+79997000002"), DateTimeOffset.UtcNow);
            await new CustomerRepository(db).AddAsync(customer, CancellationToken.None);
            stolenSlot!.Claim(customer.Id, _seed.Service.Id, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(15));
            stolenSlot.ClearDomainEvents();
            await new EventRepository(db).SaveAsync(stolenSlot, CancellationToken.None);
        }

        var afterPhone = await ReplyAsync(
            started.ExternalTaskId, ModuleStepKinds.VerifiedPhoneForm, "+79997000003", phoneVerifiedAt: DateTimeOffset.UtcNow);

        Assert.False(afterPhone.Complete);
        Assert.Equal(ModuleStepKinds.DateTimePicker, afterPhone.Step!.Kind);
        var reofferedValues = afterPhone.Step.Actions.Select(a => a.Value).ToList();
        Assert.DoesNotContain(offeredSlotValues[0], reofferedValues);
        Assert.Contains(offeredSlotValues[1], reofferedValues);

        var retry = await ReplyAsync(started.ExternalTaskId, ModuleStepKinds.DateTimePicker, offeredSlotValues[1]);
        Assert.Equal(ModuleStepKinds.VerifiedPhoneForm, retry.Step!.Kind);
        var confirmed = await ReplyAsync(
            started.ExternalTaskId, ModuleStepKinds.VerifiedPhoneForm, "+79997000003", phoneVerifiedAt: DateTimeOffset.UtcNow);
        Assert.True(confirmed.Complete);
    }

    /// <summary>
    /// `20-09`'s own defense-in-depth, over real HTTP against the real host: a phone-step reply
    /// carrying no <c>phoneVerifiedAt</c> at all never claims the slot - the same property
    /// <c>Ago.Calendar.Application.Tests.ChatModuleTaskHandlerTests.APhoneReplyWithNoVerificationAssertion_...</c>
    /// proves with fakes, proven here against the real <see cref="ReplyToModuleTaskHandler"/>, the real
    /// <see cref="BookEventHandler"/> and a real Postgres. The counterpart proof - that Chat's own real
    /// <c>HttpModuleGateway</c> never sends this request at all when no verified identity exists - lives
    /// in <c>ago-chat</c>'s own <c>Ago.Chat.Integration.Tests.ModuleTaskGatewayIntegrationTests</c>
    /// (a separate repository test, since the two products share no reference to call one test across
    /// both).
    /// </summary>
    [Fact]
    public async Task APhoneStepReply_WithNoVerificationAssertion_NeverClaimsTheSlot()
    {
        var started = (await (await StartAsync(Guid.NewGuid(), _seed.Tenant.Id.Value, Guid.NewGuid(), "/booking"))
            .Content.ReadFromJsonAsync<ModuleTaskStartResponse>())!;

        var afterService = await ReplyAsync(
            started.ExternalTaskId, ModuleStepKinds.ChoiceList, _seed.Service.Id.Value.ToString());
        var afterWorker = await ReplyAsync(
            started.ExternalTaskId, ModuleStepKinds.ChoiceList, Assert.Single(afterService.Step!.Actions).Value);
        var dateValue = Assert.Single(afterWorker.Step!.Actions).Value;
        var afterDate = await ReplyAsync(started.ExternalTaskId, ModuleStepKinds.DateTimePicker, dateValue);
        var slotValue = Assert.Single(afterDate.Step!.Actions).Value;
        var afterSlot = await ReplyAsync(started.ExternalTaskId, ModuleStepKinds.DateTimePicker, slotValue);
        Assert.Equal(ModuleStepKinds.VerifiedPhoneForm, afterSlot.Step!.Kind);

        // No phoneVerifiedAt - exactly what a caller that skipped Chat's own gate would send.
        var afterPhone = await ReplyAsync(started.ExternalTaskId, ModuleStepKinds.VerifiedPhoneForm, "+79997000009");

        // Not a dead end - the same re-offer path a lost availability race already produces.
        Assert.False(afterPhone.Complete);
        Assert.Equal(ModuleStepKinds.DateTimePicker, afterPhone.Step!.Kind);

        await using var db = fixture.CreateDbContext();
        var stillAvailable = await db.Events.SingleAsync(e => e.Id == new EventId(Guid.Parse(slotValue)));
        Assert.Equal(EventStatus.Available, stillAvailable.Status);
    }

    [Fact]
    public async Task TheStartResponse_UsesExactCamelCaseFieldNamesOnTheWire()
    {
        var response = await StartAsync(Guid.NewGuid(), _seed.Tenant.Id.Value, Guid.NewGuid(), "/booking");

        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        Assert.True(root.TryGetProperty("externalTaskId", out _));
        Assert.True(root.TryGetProperty("complete", out _));
        var step = root.GetProperty("step");
        Assert.True(step.TryGetProperty("kind", out var kind));
        Assert.Equal(ModuleStepKinds.ChoiceList, kind.GetString());
        Assert.True(step.TryGetProperty("payload", out var payload));
        Assert.True(payload.TryGetProperty("prompt", out _));
        Assert.True(step.TryGetProperty("actions", out var actions));
        Assert.True(actions[0].TryGetProperty("label", out _));
        Assert.True(actions[0].TryGetProperty("value", out _));

        // Never PascalCase anywhere on the wire - the failure mode a naming-policy misconfiguration
        // would actually produce.
        Assert.DoesNotContain("ExternalTaskId", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Kind\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReplyForAnUnknownTask_Returns404()
    {
        var response = await PostReplyAsync(
            Guid.NewGuid().ToString(), ModuleStepKinds.ChoiceList, Guid.NewGuid().ToString());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("chat_module_task.not_found", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------
    // `22-02`'s own three-directional security claim, over the real host, a real Postgres and a
    // real signature check - not the fake gateway `RouteConversationToModuleHandlerTests` (in
    // `ago-chat`) proves the *sending* half with. The distinguishable-401-vs-404 control this
    // suite's own "unknown route" test provides is `AReplyForAnUnknownTask_Returns404` above (a
    // known route, an unknown *resource*) plus `AnUnmappedSiblingRoute_Returns404` below (an
    // unknown route entirely) - `20-24`'s own lesson about the two never being conflated.
    //
    // `22-04` adds a fourth: the secret itself is now per tenant, not per deployment - proven by
    // TwoTenants_EachWithItsOwnRegistration_ResolveIndependently and
    // CredentialSignedWithOneTenantsOwnSecret_ButClaimingAnotherTenant_IsRefused below, the exact
    // regression adr/0094 named as this item's own to close.
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task StartWithNoCredentialHeader_IsRefused()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/module-tasks")
        {
            Content = JsonContent.Create(
                new ModuleTaskStartRequest(Guid.NewGuid(), _seed.Tenant.Id.Value, Guid.NewGuid(), "/booking")),
        };

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task StartWithAWrongCredential_IsRefused()
    {
        var response = await StartAsync(
            Guid.NewGuid(), _seed.Tenant.Id.Value, Guid.NewGuid(), "/booking",
            MintCredentialHeader(_seed.Tenant.Id.Value, "a-completely-different-secret-value"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>The sharpest claim: a credential that is genuinely valid - signed with this tenant's
    /// own real, registered secret - still cannot act for a site other than the one it names. This
    /// is the property that stops a body's own <c>siteId</c> from becoming the tenant selector
    /// `22-04` would otherwise hand the internet, the exact gap `docs/backlog/22-02-*` names.</summary>
    [Fact]
    public async Task StartWithACredentialForAnotherSite_IsRefused()
    {
        var differentBodySiteId = Guid.NewGuid();

        var response = await StartAsync(
            Guid.NewGuid(), differentBodySiteId, Guid.NewGuid(), "/booking",
            MintCredentialHeader(_seed.Tenant.Id.Value, TestSharedSecret));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task StartWithTheMatchingCredential_Succeeds()
    {
        var response = await StartAsync(
            Guid.NewGuid(), _seed.Tenant.Id.Value, Guid.NewGuid(), "/booking",
            MintCredentialHeader(_seed.Tenant.Id.Value, TestSharedSecret));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>A site nobody registered - no <see cref="ChatModuleRegistration"/> row at all - has
    /// no secret to be checked against, and is refused exactly like any other unauthenticated call,
    /// never answered by falling back to anybody else's tenant.</summary>
    [Fact]
    public async Task StartForAnUnregisteredSite_IsRefused()
    {
        var unregisteredSite = Guid.NewGuid();

        var response = await StartAsync(
            Guid.NewGuid(), unregisteredSite, Guid.NewGuid(), "/booking",
            MintCredentialHeader(unregisteredSite, "a-secret-nobody-ever-registered-anywhere"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>`22-04`'s own regression guard for adr/0094's named limit: under the old
    /// deployment-wide secret this exact call would have succeeded, because one secret verified every
    /// tenant. Both tenants below are real, registered rows with real, different secrets; the token
    /// merely claims to be the second tenant while signed with the first's own secret, and the
    /// signature can only ever verify against the secret the claimed tenant actually owns.</summary>
    [Fact]
    public async Task CredentialSignedWithOneTenantsOwnSecret_ButClaimingAnotherTenant_IsRefused()
    {
        var otherSeed = await CalendarSeed.WriteAsync(fixture, publicKey: $"chatmod2-{CalendarSeed.NewId():N}"[..24]);
        const string otherSecret = "a-second-tenants-own-independently-generated-secret";
        await RegisterChatModuleAsync(otherSeed.Tenant.Id, otherSecret);

        // Signed with _seed's own secret, but the payload claims to be otherSeed's tenant.
        var forgedForOther = MintCredentialHeader(otherSeed.Tenant.Id.Value, TestSharedSecret);

        var response = await StartAsync(Guid.NewGuid(), otherSeed.Tenant.Id.Value, Guid.NewGuid(), "/booking", forgedForOther);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>The Done-when's own sharpest requirement: two sites, each with the module enabled
    /// under its own independently generated secret, resolve to two different tenants - proven by each
    /// Start response carrying that tenant's own, distinct seeded service id.</summary>
    [Fact]
    public async Task TwoTenants_EachWithItsOwnRegistration_ResolveIndependently()
    {
        var otherSeed = await CalendarSeed.WriteAsync(fixture, publicKey: $"chatmod3-{CalendarSeed.NewId():N}"[..24]);
        const string otherSecret = "yet-another-tenants-own-independent-secret-value";
        await RegisterChatModuleAsync(otherSeed.Tenant.Id, otherSecret);

        var responseA = await StartAsync(
            Guid.NewGuid(), _seed.Tenant.Id.Value, Guid.NewGuid(), "/booking",
            MintCredentialHeader(_seed.Tenant.Id.Value, TestSharedSecret));
        var responseB = await StartAsync(
            Guid.NewGuid(), otherSeed.Tenant.Id.Value, Guid.NewGuid(), "/booking",
            MintCredentialHeader(otherSeed.Tenant.Id.Value, otherSecret));

        Assert.Equal(HttpStatusCode.OK, responseA.StatusCode);
        Assert.Equal(HttpStatusCode.OK, responseB.StatusCode);

        var bodyA = await responseA.Content.ReadFromJsonAsync<ModuleTaskStartResponse>();
        var bodyB = await responseB.Content.ReadFromJsonAsync<ModuleTaskStartResponse>();

        // Each tenant's own, independently seeded service id - not merely "both succeeded", but
        // "each one resolved to its own data", which is what "reaches two different tenants" means.
        var serviceA = Assert.Single(bodyA!.Step.Actions).Value;
        var serviceB = Assert.Single(bodyB!.Step.Actions).Value;
        Assert.Equal(_seed.Service.Id.Value.ToString(), serviceA);
        Assert.Equal(otherSeed.Service.Id.Value.ToString(), serviceB);
        Assert.NotEqual(serviceA, serviceB);
    }

    /// <summary>Closes the asymmetry adr/0094 named between this route and Calendar's own Start
    /// route: a credential proven for a different tenant is refused as if the task did not exist,
    /// the identical property <c>Ago.Faq.Integration.Tests</c>' own sibling test proves for that
    /// product.</summary>
    [Fact]
    public async Task ReplyWithACredentialForAnotherTenant_IsRefused_AsIfTheTaskDidNotExist()
    {
        var otherSeed = await CalendarSeed.WriteAsync(fixture, publicKey: $"chatmod4-{CalendarSeed.NewId():N}"[..24]);
        const string otherSecret = "a-fourth-tenants-own-independently-generated-secret";
        await RegisterChatModuleAsync(otherSeed.Tenant.Id, otherSecret);

        var started = (await (await StartAsync(
                Guid.NewGuid(), _seed.Tenant.Id.Value, Guid.NewGuid(), "/booking",
                MintCredentialHeader(_seed.Tenant.Id.Value, TestSharedSecret)))
            .Content.ReadFromJsonAsync<ModuleTaskStartResponse>())!;

        var response = await PostReplyAsync(
            started.ExternalTaskId, ModuleStepKinds.ChoiceList, _seed.Service.Id.Value.ToString(),
            siteId: otherSeed.Tenant.Id.Value, secret: otherSecret);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("chat_module_task.not_found", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnmappedSiblingRoute_Returns404()
    {
        // `20-24`'s own lesson, restated as a control: a refusal that *looks like* a missing route
        // makes 401 indistinguishable from "this was never mapped". This nonsense sibling path
        // proves the real route is genuinely refused (401 above), not merely unreachable (404 here).
        var response = await _client.GetAsync("/api/v1/module-tasks-nonexistent-sibling-route");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>`22-04`: seeds one <c>ChatModuleRegistration</c> row directly through the write-side
    /// repository - the registry's own consuming half, proven separately by
    /// <c>Ago.Calendar.Domain.Tests</c>; no console or provisioning endpoint exists yet to do this
    /// over HTTP (out of this item's own scope - see this item's report).</summary>
    /// <summary>`25-37`: <see cref="StepDto.Payload"/> deserializes as a raw <see cref="JsonElement"/>
    /// (it is declared <c>object</c> on the wire type, the same reason
    /// <c>TheStartResponse_UsesExactCamelCaseFieldNamesOnTheWire</c> parses the response body by hand)
    /// - this is the one place in this file that reads <c>payload.prompt</c> back out of it rather
    /// than only asserting the property exists.</summary>
    private static string Prompt(StepDto step) => ((JsonElement)step.Payload).GetProperty("prompt").GetString()!;

    private async Task RegisterChatModuleAsync(TenantId tenantId, string secret)
    {
        await using var db = fixture.CreateDbContext();
        await new ChatModuleRegistrationRepository(db).AddAsync(
            ChatModuleRegistration.Register(tenantId, new ChatModuleCredential(secret), CalendarSeed.Now),
            CancellationToken.None);
    }

    private async Task<HttpResponseMessage> StartAsync(
        Guid chatTaskId, Guid siteId, Guid conversationId, string triggerText, string? credentialHeader = null,
        string locale = "En")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/module-tasks")
        {
            Content = JsonContent.Create(
                new ModuleTaskStartRequest(chatTaskId, siteId, conversationId, triggerText, locale)),
        };
        request.Headers.Add("X-Ago-Module-Credential", credentialHeader ?? MintCredentialHeader(siteId, TestSharedSecret));
        // Awaited here, not returned as a bare Task - `request` is disposed by this method's own
        // `using` the moment it returns, and disposing it before SendAsync has finished reading its
        // content throws ObjectDisposedException from inside the TestServer pipeline (found by this
        // item's own fails-before run, not by inspection).
        return await _client.SendAsync(request);
    }

    /// <summary>`25-37`/`25-38`/`25-39`: <paramref name="locale"/>/<paramref name="knownPhone"/>/
    /// <paramref name="acceptUnverifiedPhone"/> default to English/none-known/off - today's
    /// behaviour, matching every existing test in this file that never mentions them.</summary>
    private async Task<ModuleTaskReplyResponse> ReplyAsync(
        string externalTaskId, string kind, string value, DateTimeOffset? phoneVerifiedAt = null,
        string locale = "En", string? knownPhone = null, bool acceptUnverifiedPhone = false)
    {
        var response = await PostReplyAsync(
            externalTaskId, kind, value, phoneVerifiedAt, locale: locale, knownPhone: knownPhone,
            acceptUnverifiedPhone: acceptUnverifiedPhone);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ModuleTaskReplyResponse>())!;
    }

    /// <summary>`22-04`: <paramref name="siteId"/>/<paramref name="secret"/> default to this suite's
    /// own seeded tenant and its registered secret - every existing test in this file replies to a
    /// task it started under that same tenant, so this is the happy path. A caller passes a different
    /// pair to prove the cross-tenant refusal (<see cref="ReplyWithACredentialForAnotherTenant_IsRefused_AsIfTheTaskDidNotExist"/>).</summary>
    private async Task<HttpResponseMessage> PostReplyAsync(
        string externalTaskId, string kind, string value, DateTimeOffset? phoneVerifiedAt = null,
        Guid? siteId = null, string? secret = null, string locale = "En", string? knownPhone = null,
        bool acceptUnverifiedPhone = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/module-tasks/{externalTaskId}/replies")
        {
            Content = JsonContent.Create(
                new ModuleTaskReplyRequest(
                    Guid.NewGuid(), kind, value, phoneVerifiedAt, locale, knownPhone, acceptUnverifiedPhone)),
        };
        request.Headers.Add(
            "X-Ago-Module-Credential",
            MintCredentialHeader(siteId ?? _seed.Tenant.Id.Value, secret ?? TestSharedSecret));
        return await _client.SendAsync(request);
    }

    /// <summary>`22-02`/`22-04`: this suite's own independent re-derivation of the wire format
    /// <c>HmacModuleCallCredentialValidator</c> checks - written from the contract's own description
    /// (see that class's remarks), not by calling production code, so this test would catch the
    /// validator disagreeing with its own documented contract rather than merely agreeing with
    /// itself.</summary>
    private static string MintCredentialHeader(Guid siteId, string secret, TimeSpan? expiresIn = null)
    {
        var now = DateTimeOffset.UtcNow;
        var exp = now.Add(expiresIn ?? TimeSpan.FromSeconds(60));
        var payloadJson = JsonSerializer.Serialize(
            new TestPayload(siteId, now.ToUnixTimeSeconds(), exp.ToUnixTimeSeconds()),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var encodedPayload = Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson));
        var signature = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(encodedPayload));
        return $"{encodedPayload}.{Base64UrlEncode(signature)}";
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record TestPayload(
        [property: JsonPropertyName("siteId")] Guid SiteId,
        [property: JsonPropertyName("iat")] long Iat,
        [property: JsonPropertyName("exp")] long Exp);
}
