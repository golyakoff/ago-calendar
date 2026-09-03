using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    /// <summary>`22-02`: this suite's own shared secret, matching what <see cref="ChatModuleApiFactory"/>
    /// configures as <c>ChatModule:SharedSecret</c> - the same "the two sides are configured with the
    /// identical string" manual pairing a real deployment's operator would do between this host and its
    /// `ago-chat` registration (<c>Ago.Chat.Domain.ModuleCredential</c>'s own remarks).</summary>
    private const string TestSharedSecret = "integration-test-shared-secret-of-sufficient-length";

    private ChatModuleApiFactory _factory = null!;
    private HttpClient _client = null!;
    private SeededTenant _seed = null!;

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
        var slot = CalendarSeed.Slot(_seed, DateTimeOffset.UtcNow.AddDays(7));
        await using (var db = fixture.CreateDbContext())
        {
            await new EventRepository(db).AddRangeAsync([slot], CancellationToken.None);
        }

        _factory = new ChatModuleApiFactory(fixture, _seed.Tenant.PublicKey.Value, _seed.Calendar.Id.Value, TestSharedSecret);
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
        var startResponse = await StartAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "/booking");
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

        var started = (await (await StartAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "/booking"))
            .Content.ReadFromJsonAsync<ModuleTaskStartResponse>())!;

        var afterService = await ReplyAsync(
            started.ExternalTaskId, ModuleStepKinds.ChoiceList, _seed.Service.Id.Value.ToString());
        var afterWorker = await ReplyAsync(
            started.ExternalTaskId, ModuleStepKinds.ChoiceList, Assert.Single(afterService.Step!.Actions).Value);
        var offeredSlotValues = afterWorker.Step!.Actions.Select(a => a.Value).ToList();
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
        var started = (await (await StartAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "/booking"))
            .Content.ReadFromJsonAsync<ModuleTaskStartResponse>())!;

        var afterService = await ReplyAsync(
            started.ExternalTaskId, ModuleStepKinds.ChoiceList, _seed.Service.Id.Value.ToString());
        var afterWorker = await ReplyAsync(
            started.ExternalTaskId, ModuleStepKinds.ChoiceList, Assert.Single(afterService.Step!.Actions).Value);
        var slotValue = Assert.Single(afterWorker.Step!.Actions).Value;
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
        var response = await StartAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "/booking");

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
        var response = await PostReplyAsync(Guid.NewGuid().ToString(), ModuleStepKinds.ChoiceList, Guid.NewGuid().ToString());

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
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task StartWithNoCredentialHeader_IsRefused()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/module-tasks")
        {
            Content = JsonContent.Create(new ModuleTaskStartRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "/booking")),
        };

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task StartWithAWrongCredential_IsRefused()
    {
        var siteId = Guid.NewGuid();
        var response = await StartAsync(
            Guid.NewGuid(), siteId, Guid.NewGuid(), "/booking", MintCredentialHeader(siteId, "a-completely-different-secret-value"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>The sharpest claim: a credential that is genuinely valid - signed with this
    /// deployment's own real secret - still cannot act for a site other than the one it names. This
    /// is the property that stops a body's own <c>siteId</c> from becoming the tenant selector
    /// `22-04` would otherwise hand the internet, the exact gap `docs/backlog/22-02-*` names.</summary>
    [Fact]
    public async Task StartWithACredentialForAnotherSite_IsRefused()
    {
        var credentialedSiteId = Guid.NewGuid();
        var differentBodySiteId = Guid.NewGuid();

        var response = await StartAsync(
            Guid.NewGuid(), differentBodySiteId, Guid.NewGuid(), "/booking",
            MintCredentialHeader(credentialedSiteId, TestSharedSecret));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task StartWithTheMatchingCredential_Succeeds()
    {
        var siteId = Guid.NewGuid();
        var response = await StartAsync(Guid.NewGuid(), siteId, Guid.NewGuid(), "/booking", MintCredentialHeader(siteId, TestSharedSecret));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
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

    private async Task<HttpResponseMessage> StartAsync(
        Guid chatTaskId, Guid siteId, Guid conversationId, string triggerText, string? credentialHeader = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/module-tasks")
        {
            Content = JsonContent.Create(new ModuleTaskStartRequest(chatTaskId, siteId, conversationId, triggerText)),
        };
        request.Headers.Add("X-Ago-Module-Credential", credentialHeader ?? MintCredentialHeader(siteId, TestSharedSecret));
        // Awaited here, not returned as a bare Task - `request` is disposed by this method's own
        // `using` the moment it returns, and disposing it before SendAsync has finished reading its
        // content throws ObjectDisposedException from inside the TestServer pipeline (found by this
        // item's own fails-before run, not by inspection).
        return await _client.SendAsync(request);
    }

    private async Task<ModuleTaskReplyResponse> ReplyAsync(
        string externalTaskId, string kind, string value, DateTimeOffset? phoneVerifiedAt = null)
    {
        var response = await PostReplyAsync(externalTaskId, kind, value, phoneVerifiedAt);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ModuleTaskReplyResponse>())!;
    }

    private async Task<HttpResponseMessage> PostReplyAsync(
        string externalTaskId, string kind, string value, DateTimeOffset? phoneVerifiedAt = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/module-tasks/{externalTaskId}/replies")
        {
            Content = JsonContent.Create(new ModuleTaskReplyRequest(Guid.NewGuid(), kind, value, phoneVerifiedAt)),
        };
        // The reply route never cross-checks a site id (ChatModuleTaskEndpoints's own remarks), so
        // any site claim in a genuinely valid credential authenticates every reply in this suite.
        request.Headers.Add("X-Ago-Module-Credential", MintCredentialHeader(Guid.NewGuid(), TestSharedSecret));
        return await _client.SendAsync(request);
    }

    /// <summary>`22-02`: this suite's own independent re-derivation of the wire format
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

/// <summary>
/// <see cref="CalendarApiFactory"/> with <c>ChatModule:*</c> pointed at this test class's own seeded
/// tenant and calendar - the static-wiring config this item's own backlog entry names, supplied per
/// test the same way <c>BookingApiFactory</c> supplies a tuned rate limit.
/// </summary>
internal sealed class ChatModuleApiFactory(PostgresFixture fixture, string tenantPublicKey, Guid calendarId, string sharedSecret)
    : CalendarApiFactory(fixture)
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.UseSetting("ChatModule:TenantPublicKey", tenantPublicKey);
        builder.UseSetting("ChatModule:CalendarId", calendarId.ToString());

        // `22-02`: the matching half of ChatModuleTaskEndpointTests's own TestSharedSecret -
        // RequireCredential is left at its own default (true), matching what this host actually
        // ships with.
        builder.UseSetting("ChatModule:SharedSecret", sharedSecret);
    }
}
