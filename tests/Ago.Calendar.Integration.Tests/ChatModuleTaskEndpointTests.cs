using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ago.Calendar.Contracts;
using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
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
/// <para><b>The wire's exact casing is asserted against the real serialized bytes</b>, not against a
/// hand-rolled <c>JsonSerializerOptions</c> copy that could quietly drift from what
/// <c>Ago.Calendar.Api</c> actually ships - the same reasoning <c>BookingEndpointTests</c> already
/// applies to its own "no 'pending' anywhere in this payload" assertion.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public class ChatModuleTaskEndpointTests(PostgresFixture fixture) : IAsyncLifetime
{
    private ChatModuleApiFactory _factory = null!;
    private HttpClient _client = null!;
    private SeededTenant _seed = null!;

    public async Task InitializeAsync()
    {
        _seed = await CalendarSeed.WriteAsync(fixture, publicKey: $"chatmod-{CalendarSeed.NewId():N}"[..24]);

        // Slots are inserted directly rather than through the materialiser (`20-02`) - the same
        // shortcut BookingEndpointTests' own BookableSlotsAsync takes, since this suite is about the
        // chat-module wire contract, not availability generation.
        //
        // Far enough ahead that "starts_at > now" holds against the host's own real clock - this
        // test cannot inject a fake one, exactly like BookingEndpointTests' own slots.
        var slot = CalendarSeed.Slot(_seed, DateTimeOffset.UtcNow.AddDays(7));
        await using (var db = fixture.CreateDbContext())
        {
            await new EventRepository(db).AddRangeAsync([slot], CancellationToken.None);
        }

        _factory = new ChatModuleApiFactory(fixture, _seed.Tenant.PublicKey.Value, _seed.Calendar.Id.Value);
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
        var startResponse = await _client.PostAsJsonAsync(
            "/api/v1/module-tasks",
            new ModuleTaskStartRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "/booking"));
        Assert.Equal(HttpStatusCode.OK, startResponse.StatusCode);

        var started = await startResponse.Content.ReadFromJsonAsync<ModuleTaskStartResponse>();
        Assert.False(started!.Complete);
        Assert.Equal(ModuleStepKinds.ChoiceList, started.Step.Kind);
        var serviceAction = Assert.Single(started.Step.Actions);
        Assert.Equal(_seed.Service.Id.Value.ToString(), serviceAction.Value);

        var afterService = await ReplyAsync(
            started.ExternalTaskId, ModuleStepKinds.ChoiceList, serviceAction.Value);
        Assert.Equal(ModuleStepKinds.ChoiceList, afterService.Step!.Kind);
        var workerAction = Assert.Single(afterService.Step.Actions);
        Assert.Equal(_seed.Worker.Id.Value.ToString(), workerAction.Value);

        var afterWorker = await ReplyAsync(started.ExternalTaskId, ModuleStepKinds.ChoiceList, workerAction.Value);
        Assert.Equal(ModuleStepKinds.DateTimePicker, afterWorker.Step!.Kind);
        var slotAction = Assert.Single(afterWorker.Step.Actions);

        var afterSlot = await ReplyAsync(started.ExternalTaskId, ModuleStepKinds.DateTimePicker, slotAction.Value);
        Assert.Equal(ModuleStepKinds.Form, afterSlot.Step!.Kind);

        var afterPhone = await ReplyAsync(started.ExternalTaskId, ModuleStepKinds.Form, "+79997000001");
        Assert.True(afterPhone.Complete);
        Assert.Equal(ModuleStepKinds.ConfirmationCard, afterPhone.Step!.Kind);

        // The real row, not a fake's own record of an attempt.
        await using var db = fixture.CreateDbContext();
        var bookedEventId = new EventId(Guid.Parse(slotAction.Value));
        var stored = await db.Events.SingleAsync(e => e.Id == bookedEventId);
        Assert.Equal(EventStatus.PendingConfirmation, stored.Status);
        Assert.NotNull(stored.CustomerId);
    }

    [Fact]
    public async Task AFailedBookingAttempt_ReOffersFreshSlots_AndTheSecondAttemptCanStillSucceed()
    {
        // A second slot for the same worker, so there is something left to re-offer once the first
        // is claimed out from under this task by somebody else.
        var secondSlot = CalendarSeed.Slot(_seed, DateTimeOffset.UtcNow.AddDays(7).AddHours(2));
        await using (var db = fixture.CreateDbContext())
        {
            await new EventRepository(db).AddRangeAsync([secondSlot], CancellationToken.None);
        }

        var started = (await (await _client.PostAsJsonAsync(
                "/api/v1/module-tasks",
                new ModuleTaskStartRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "/booking")))
            .Content.ReadFromJsonAsync<ModuleTaskStartResponse>())!;

        var afterService = await ReplyAsync(
            started.ExternalTaskId, ModuleStepKinds.ChoiceList, _seed.Service.Id.Value.ToString());
        var afterWorker = await ReplyAsync(
            started.ExternalTaskId, ModuleStepKinds.ChoiceList, Assert.Single(afterService.Step!.Actions).Value);
        var offeredSlotValues = afterWorker.Step!.Actions.Select(a => a.Value).ToList();
        Assert.Equal(2, offeredSlotValues.Count);

        var afterSlot = await ReplyAsync(started.ExternalTaskId, ModuleStepKinds.DateTimePicker, offeredSlotValues[0]);
        Assert.Equal(ModuleStepKinds.Form, afterSlot.Step!.Kind);

        // Somebody else takes that exact slot before the phone number arrives - a real race, not a
        // fake flag, using the same public booking endpoint a widget would.
        var stolen = await _client.PostAsJsonAsync(
            $"/api/v1/calendars/{_seed.Calendar.Id.Value}/events/{offeredSlotValues[0]}/book",
            new BookEventRequest(_seed.Service.Id.Value, "+79997000002", null));
        Assert.Equal(HttpStatusCode.OK, stolen.StatusCode);

        var afterPhone = await ReplyAsync(started.ExternalTaskId, ModuleStepKinds.Form, "+79997000003");

        Assert.False(afterPhone.Complete);
        Assert.Equal(ModuleStepKinds.DateTimePicker, afterPhone.Step!.Kind);
        var reofferedValues = afterPhone.Step.Actions.Select(a => a.Value).ToList();
        Assert.DoesNotContain(offeredSlotValues[0], reofferedValues);
        Assert.Contains(offeredSlotValues[1], reofferedValues);

        var retry = await ReplyAsync(started.ExternalTaskId, ModuleStepKinds.DateTimePicker, offeredSlotValues[1]);
        Assert.Equal(ModuleStepKinds.Form, retry.Step!.Kind);
        var confirmed = await ReplyAsync(started.ExternalTaskId, ModuleStepKinds.Form, "+79997000003");
        Assert.True(confirmed.Complete);
    }

    [Fact]
    public async Task TheStartResponse_UsesExactCamelCaseFieldNamesOnTheWire()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/v1/module-tasks",
            new ModuleTaskStartRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "/booking"));

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
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/module-tasks/{Guid.NewGuid()}/replies",
            new ModuleTaskReplyRequest(Guid.NewGuid(), ModuleStepKinds.ChoiceList, Guid.NewGuid().ToString()));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("chat_module_task.not_found", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheEndpointsRequireNoAuthentication()
    {
        // Stated as a test because it is a deliberate, named gap rather than an omission - see
        // ChatModuleTaskEndpoints's own remarks: no service-to-service auth exists in either
        // direction yet.
        Assert.DoesNotContain(_client.DefaultRequestHeaders, header => header.Key == "Authorization");

        var response = await _client.PostAsJsonAsync(
            "/api/v1/module-tasks",
            new ModuleTaskStartRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "/booking"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<ModuleTaskReplyResponse> ReplyAsync(string externalTaskId, string kind, string value)
    {
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/module-tasks/{externalTaskId}/replies",
            new ModuleTaskReplyRequest(Guid.NewGuid(), kind, value));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ModuleTaskReplyResponse>())!;
    }
}

/// <summary>
/// <see cref="CalendarApiFactory"/> with <c>ChatModule:*</c> pointed at this test class's own seeded
/// tenant and calendar - the static-wiring config this item's own backlog entry names, supplied per
/// test the same way <c>BookingApiFactory</c> supplies a tuned rate limit.
/// </summary>
internal sealed class ChatModuleApiFactory(PostgresFixture fixture, string tenantPublicKey, Guid calendarId)
    : CalendarApiFactory(fixture)
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.UseSetting("ChatModule:TenantPublicKey", tenantPublicKey);
        builder.UseSetting("ChatModule:CalendarId", calendarId.ToString());
    }
}
