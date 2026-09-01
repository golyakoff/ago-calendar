using System.Net;
using System.Net.Http.Json;
using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Contracts;
using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres;
using Ago.Calendar.Module.PhoneVerification;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ago.Calendar.Integration.Tests;

/// <summary>
/// `20-10`'s own headline Done-when box, proven live: "A first-time visitor with no prior verified
/// phone completes a real booking through the public widget end to end - a real code is generated,
/// hashed, stored, retrieved (via the fake sender's log/dev surface), submitted and confirmed."
///
/// <para>Everything below runs against the real <c>Ago.Calendar.Api</c> host, a real Postgres, and the
/// real <see cref="FakePhoneVerificationSender"/> - the one honestly-named gap is that no real SMS/voice
/// call is placed, which this file's own use of <see cref="PhoneVerificationDevEndpoints"/> (rather than
/// scraping a log) makes explicit rather than hidden.</para>
///
/// <para>Phone numbers are invented <c>+7999...</c> values belonging to nobody
/// (<c>personal-data.md</c>).</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public class PhoneVerificationEndpointTests(PostgresFixture fixture) : IAsyncLifetime
{
    private PhoneVerificationApiFactory _factory = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _factory = new PhoneVerificationApiFactory(fixture);
        _client = _factory.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task AFirstTimeVisitor_CompletesARealBookingThroughThePublicWidget_EndToEnd()
    {
        var (seed, slot) = await ABookableSlotAsync();
        const string phone = "+79996200001";

        // 1. Initiate - a real PendingPhoneVerification row is created, and the fake sender "sends"
        // (logs) a real, freshly generated code. Nothing here is faked except the SMS/voice call itself.
        var initiated = await InitiateAsync(seed, phone);
        Assert.Equal(HttpStatusCode.Created, initiated.StatusCode);
        var initiatedBody = await initiated.Content.ReadFromJsonAsync<InitiatedPhoneVerificationResponse>();
        Assert.NotNull(initiatedBody);
        Assert.Equal("Sms", initiatedBody!.DeliveryMethod);

        // Real code, real hash, real row - verified directly against the database, not just against
        // the dev endpoint that is about to read the identical value back.
        await using (var db = fixture.CreateDbContext())
        {
            var stored = await db.PendingPhoneVerifications.SingleAsync(
                p => p.Id == new PendingPhoneVerificationId(initiatedBody.PendingPhoneVerificationId));
            Assert.Equal(phone, stored.Phone);
            Assert.Null(stored.ConsumedAt);
        }

        // 2. Retrieve the code - the fake sender's own dev surface, standing in for a person reading an
        // SMS off their own phone. This is the one honestly-named gap: no real SMS is sent.
        var codeResponse = await _client.GetAsync($"/dev/phone-verifications/last-code?phone={Uri.EscapeDataString(phone)}");
        Assert.Equal(HttpStatusCode.OK, codeResponse.StatusCode);
        var codeBody = await codeResponse.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        var code = codeBody!["code"];
        Assert.Matches("^[0-9]{6}$", code);

        // 3. Confirm - the real domain logic (PendingPhoneVerification.AttemptConfirm) checks the real
        // hash, and a fresh, unforgeable proof token is minted.
        var confirmed = await ConfirmAsync(seed, initiatedBody.PendingPhoneVerificationId, code);
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        var confirmedBody = await confirmed.Content.ReadFromJsonAsync<ConfirmedPhoneVerificationResponse>();
        Assert.NotNull(confirmedBody);
        Assert.False(string.IsNullOrWhiteSpace(confirmedBody!.ProofToken));

        // 4. Book - BookEvent.RequiresVerifiedPhone is true and the proof resolves to a real instant,
        // so the claim succeeds exactly like a chat-originated booking always could.
        var bookResponse = await _client.PostAsJsonAsync(
            $"/api/v1/calendars/{seed.Calendar.Id.Value}/events/{slot.Id.Value}/book",
            new BookEventRequest(
                seed.Service.Id.Value, phone, "Anna", confirmedBody.PendingPhoneVerificationId, confirmedBody.ProofToken));

        Assert.Equal(HttpStatusCode.OK, bookResponse.StatusCode);
        var booked = await bookResponse.Content.ReadFromJsonAsync<BookingConfirmedResponse>();
        Assert.Equal(slot.Id.Value, booked!.BookingId);

        // The snapshot this item's own Scope also asks for: a phone verified through this mechanism
        // now satisfies BookingStore's own "returning customer" shortcut for a future booking too.
        await using (var db = fixture.CreateDbContext())
        {
            var customer = await db.Customers.SingleAsync(c => c.TenantId == seed.Tenant.Id && c.Phone == new PhoneNumber(phone));
            Assert.NotNull(customer.PhoneVerifiedAt);
        }
    }

    [Fact]
    public async Task AWrongCode_IsRefusedWith400()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        const string phone = "+79996200002";
        var initiated = await InitiateAsync(seed, phone);
        var body = await initiated.Content.ReadFromJsonAsync<InitiatedPhoneVerificationResponse>();

        var response = await ConfirmAsync(seed, body!.PendingPhoneVerificationId, "000000");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("phone_verification.wrong_code", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALockedOutCode_IsRefusedWith429AfterEnoughWrongAttempts()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        const string phone = "+79996200003";
        var initiated = await InitiateAsync(seed, phone);
        var id = (await initiated.Content.ReadFromJsonAsync<InitiatedPhoneVerificationResponse>())!.PendingPhoneVerificationId;

        // PhoneVerificationOptions.MaxAttempts defaults to 5.
        for (var i = 0; i < 5; i++)
        {
            await ConfirmAsync(seed, id, "000000");
        }

        var response = await ConfirmAsync(seed, id, "000000");

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Contains("phone_verification.locked_out", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    /// <summary>`20-10`'s own Done-when: "a forged/missing token is refused the same way `20-09`'s own
    /// chat-side gate refuses one." A real, confirmed proof, tampered with before it is presented to
    /// the booking endpoint.</summary>
    [Fact]
    public async Task AForgedProofToken_IsRejectedAtBookingTime()
    {
        var (seed, slot) = await ABookableSlotAsync();
        const string phone = "+79996200004";
        var (id, _) = await InitiateAndConfirmAsync(seed, phone);

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/calendars/{seed.Calendar.Id.Value}/events/{slot.Id.Value}/book",
            new BookEventRequest(seed.Service.Id.Value, phone, "Anna", id, "this-was-never-the-real-token"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("booking.phone_not_verified", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        await using var db = fixture.CreateDbContext();
        Assert.Equal(EventStatus.Available, (await db.Events.FindAsync(slot.Id))!.Status);
    }

    /// <summary>The critical security property `20-10`'s own backlog file names verbatim: a correct,
    /// genuinely-issued token, presented for a phone number other than the one it was verified
    /// for.</summary>
    [Fact]
    public async Task ACorrectProofToken_PresentedForADifferentPhone_IsRejected()
    {
        var (seed, slot) = await ABookableSlotAsync();
        var (id, proofToken) = await InitiateAndConfirmAsync(seed, "+79996200005");

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/calendars/{seed.Calendar.Id.Value}/events/{slot.Id.Value}/book",
            // A different phone number than the one verified above.
            new BookEventRequest(seed.Service.Id.Value, "+79996200006", "Anna", id, proofToken));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("booking.phone_not_verified", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    /// <summary>`20-10`'s own Done-when box, verified against the real host's own DI container rather
    /// than only by reading <c>CalendarModule.cs</c> - `ago-chat`'s own equivalent test does not exist
    /// (checked; only <c>UnconfiguredPhoneVerificationSender</c>'s unit behaviour is tested there), so
    /// this is a genuinely new check, not a mirrored one.</summary>
    [Fact]
    public void FakePhoneVerificationSender_IsTheOnlyRegisteredSender()
    {
        using var scope = _factory.Services.CreateScope();

        var senders = scope.ServiceProvider.GetServices<IPhoneVerificationSender>().ToList();

        var sender = Assert.Single(senders);
        Assert.IsType<FakePhoneVerificationSender>(sender);
    }

    private async Task<(Guid PendingPhoneVerificationId, string ProofToken)> InitiateAndConfirmAsync(
        SeededTenant seed, string phone)
    {
        var initiated = await InitiateAsync(seed, phone);
        var id = (await initiated.Content.ReadFromJsonAsync<InitiatedPhoneVerificationResponse>())!.PendingPhoneVerificationId;

        var codeResponse = await _client.GetAsync($"/dev/phone-verifications/last-code?phone={Uri.EscapeDataString(phone)}");
        var code = (await codeResponse.Content.ReadFromJsonAsync<Dictionary<string, string>>())!["code"];

        var confirmed = await ConfirmAsync(seed, id, code);
        var body = await confirmed.Content.ReadFromJsonAsync<ConfirmedPhoneVerificationResponse>();
        return (id, body!.ProofToken);
    }

    private Task<HttpResponseMessage> InitiateAsync(SeededTenant seed, string phone) =>
        _client.PostAsJsonAsync(
            $"/api/v1/calendars/{seed.Calendar.Id.Value}/phone-verifications",
            new InitiatePhoneVerificationRequest(phone));

    private Task<HttpResponseMessage> ConfirmAsync(SeededTenant seed, Guid pendingPhoneVerificationId, string code) =>
        _client.PostAsJsonAsync(
            $"/api/v1/calendars/{seed.Calendar.Id.Value}/phone-verifications/{pendingPhoneVerificationId}/confirm",
            new ConfirmPhoneVerificationRequest(code));

    private async Task<(SeededTenant Seed, Event Slot)> ABookableSlotAsync()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        await CalendarSeed.AddWeeklyScheduleAsync(fixture, seed, horizonDays: 30);

        var slot = CalendarSeed.Slot(seed, DateTimeOffset.UtcNow.AddDays(7));

        await using var db = fixture.CreateDbContext();
        await new EventRepository(db).AddRangeAsync([slot], CancellationToken.None);
        return (seed, slot);
    }
}

/// <summary>
/// <see cref="CalendarApiFactory"/>, widened for this file's own concern the same way
/// <c>BookingApiFactory</c> widens <c>BookingRateLimit:*</c>: several initiate/confirm/book round trips
/// per test must never collide with a bucket sized for production abuse. <c>UseEnvironment("Development")</c>
/// is what maps <see cref="PhoneVerificationDevEndpoints"/> at all - see <c>Program.cs</c>'s own gate.
/// </summary>
internal sealed class PhoneVerificationApiFactory(PostgresFixture fixture) : CalendarApiFactory(fixture)
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.UseEnvironment("Development");

        builder.UseSetting("PhoneVerificationRateLimit:PerPhoneCapacity", "100000");
        builder.UseSetting("PhoneVerificationRateLimit:PerPhoneRefillPerSecond", "100000");
        builder.UseSetting("PhoneVerificationRateLimit:PerIpCapacity", "100000");
        builder.UseSetting("PhoneVerificationRateLimit:PerIpRefillPerSecond", "100000");
        builder.UseSetting("PhoneVerificationRateLimit:PerCalendarCapacity", "100000");
        builder.UseSetting("PhoneVerificationRateLimit:PerCalendarRefillPerSecond", "100000");
    }
}
