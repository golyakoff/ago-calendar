using System.Net;
using System.Net.Http.Json;
using Ago.Calendar.Contracts;
using Ago.Calendar.Domain;
using Microsoft.EntityFrameworkCore;

namespace Ago.Calendar.Integration.Tests;

/// <summary>
/// `20-08`, adr/0088: the whole invite-a-colleague flow and the authority it grants, over real HTTP
/// against a real Postgres.
///
/// <para><b>Why this is its own file rather than more cases in <c>AccessControlEndpointTests</c>.</b>
/// That file is `20-12`'s own surface (roles, grant/revoke on an already-real operator). This item's
/// own Done-when is about a different moment - an operator that does not exist yet as a resolvable
/// identity - and mixing the two would bury the refusal tests this item exists to prove among tests
/// that assume an operator is already there.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public class ChatOperatorBookingAuthorityTests(PostgresFixture fixture) : IAsyncLifetime
{
    private ConsoleApiFactory _factory = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _factory = new ConsoleApiFactory(fixture);
        _client = _factory.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    // ---- Done-when: no path creates or mutates an Operator as a side effect of acting on a booking ----

    [Fact]
    public async Task ABookingAction_FromASubjectWithNoOperatorRowAtAll_IsRefused_AndCreatesNoOperator()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        var booking = await APendingBookingAsync(seed);

        await using var before = fixture.CreateDbContext();
        var operatorsBefore = await before.Operators.CountAsync();

        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"/api/v1/console/bookings/{booking.Id.Value}/cancel");
        request.Headers.Add(ConsoleApiFactory.SubjectHeader, $"kc-stranger-{CalendarSeed.NewId():N}");
        var response = await _client.SendAsync(request);

        // 403, not 404/500: the policy refuses the principal before any handler runs.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        await using var after = fixture.CreateDbContext();
        var operatorsAfter = await after.Operators.CountAsync();
        Assert.Equal(operatorsBefore, operatorsAfter);

        // The booking itself is untouched too - the refusal happened at the door, not partway through.
        var stillPending = await after.Events.FirstAsync(e => e.Id == booking.Id);
        Assert.Equal(EventStatus.PendingConfirmation, stillPending.Status);
    }

    [Fact]
    public async Task ABookingAction_FromASubjectWithAnEmailThatMatchesNoInvitedRow_IsRefused_AndCreatesNoOperator()
    {
        // The email fallback itself must not manufacture a match out of nothing - a stranger's token
        // carrying some unrelated email is exactly as refused as one carrying none.
        var seed = await CalendarSeed.WriteAsync(fixture);
        var booking = await APendingBookingAsync(seed);

        await using var before = fixture.CreateDbContext();
        var operatorsBefore = await before.Operators.CountAsync();

        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"/api/v1/console/bookings/{booking.Id.Value}/cancel");
        request.Headers.Add(ConsoleApiFactory.SubjectHeader, $"kc-stranger-{CalendarSeed.NewId():N}");
        request.Headers.Add(HeaderSubjectAuthenticationHandler.EmailHeader, "nobody-invited-this-address@example.com");
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        await using var after = fixture.CreateDbContext();
        Assert.Equal(operatorsBefore, await after.Operators.CountAsync());
    }

    // ---- Done-when: invite -> grant roles before first sign-in -> first sign-in links -> Invited->Active ----

    [Fact]
    public async Task InviteLifecycle_EndToEnd_InviteGrantSignInAndNeverMatched()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);

        var dispatcherRoleId = await CreatedIdAsync(
            "/api/v1/console/roles",
            new CreateRoleRequest("Dispatcher", [Permission.BookingCancel.Value]),
            seed,
            "roleId");

        // 1. Invite creates an unlinked, non-owner row.
        var invitedId = await CreatedIdAsync(
            "/api/v1/console/operators",
            new InviteOperatorRequest("Robin", "Robin@Shop.example"),
            seed,
            "operatorId");

        var afterInvite = await GetAsync<OperatorResponse[]>("/api/v1/console/operators", seed);
        var invitedRow = Assert.Single(afterInvite, o => o.OperatorId == invitedId);
        Assert.True(invitedRow.IsInvited);
        Assert.False(invitedRow.IsAccountOwner);
        Assert.Equal("robin@shop.example", invitedRow.InvitedEmail);
        Assert.Empty(invitedRow.RoleIds);

        // 2. Roles are grantable before the person has ever signed in.
        var granted = await PostAsync($"/api/v1/console/operators/{invitedId}/roles/{dispatcherRoleId}", null, seed);
        Assert.Equal(HttpStatusCode.NoContent, granted.StatusCode);

        var afterGrant = await GetAsync<OperatorResponse[]>("/api/v1/console/operators", seed);
        var stillInvited = Assert.Single(afterGrant, o => o.OperatorId == invitedId);
        Assert.True(stillInvited.IsInvited);
        Assert.Contains(dispatcherRoleId, stillInvited.RoleIds);

        // 3. First sign-in: sub unknown, email matches the invited row -> links, and the role granted
        // earlier is already usable through it (proves the grant was real, not merely displayed).
        var booking = await APendingBookingAsync(seed);
        var newSubject = $"kc-robin-{CalendarSeed.NewId():N}";

        using var firstSignIn = new HttpRequestMessage(
            HttpMethod.Post, $"/api/v1/console/bookings/{booking.Id.Value}/cancel");
        firstSignIn.Headers.Add(ConsoleApiFactory.SubjectHeader, newSubject);
        firstSignIn.Headers.Add(HeaderSubjectAuthenticationHandler.EmailHeader, "robin@shop.example");
        var firstSignInResponse = await _client.SendAsync(firstSignIn);
        Assert.Equal(HttpStatusCode.NoContent, firstSignInResponse.StatusCode);

        // 4. Invited -> Active, and no new operator row was created - the same row simply resolved.
        var afterSignIn = await GetAsync<OperatorResponse[]>("/api/v1/console/operators", seed);
        var activeRow = Assert.Single(afterSignIn, o => o.OperatorId == invitedId);
        Assert.False(activeRow.IsInvited);
        Assert.Equal("robin@shop.example", activeRow.InvitedEmail);
        Assert.Contains(dispatcherRoleId, activeRow.RoleIds);

        await using var db = fixture.CreateDbContext();
        var linked = await db.Operators.FirstAsync(o => o.Id == new OperatorId(invitedId));
        Assert.Equal(newSubject, linked.ExternalSubjectId);
    }

    [Fact]
    public async Task AnInvitedOperator_WhoseEmailNeverMatchesAnyone_StaysInvitedForever()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);

        var invitedId = await CreatedIdAsync(
            "/api/v1/console/operators",
            new InviteOperatorRequest("Robin", "robin@example.com"),
            seed,
            "operatorId");

        // Somebody else entirely signs in - a real, resolvable action - and it must have zero effect
        // on the still-unmatched invite.
        var booking = await APendingBookingAsync(seed);
        using var unrelated = new HttpRequestMessage(HttpMethod.Get, "/api/v1/console/pending-bookings");
        unrelated.Headers.Add(ConsoleApiFactory.SubjectHeader, seed.Operator.ExternalSubjectId);
        var unrelatedResponse = await _client.SendAsync(unrelated);
        Assert.Equal(HttpStatusCode.OK, unrelatedResponse.StatusCode);

        var operators = await GetAsync<OperatorResponse[]>("/api/v1/console/operators", seed);
        var stillInvited = Assert.Single(operators, o => o.OperatorId == invitedId);
        Assert.True(stillInvited.IsInvited);
        Assert.Equal("robin@example.com", stillInvited.InvitedEmail);

        await using var db = fixture.CreateDbContext();
        var row = await db.Operators.FirstAsync(o => o.Id == new OperatorId(invitedId));
        Assert.Null(row.ExternalSubjectId);

        // Keep the booking var alive for readability - not otherwise used, no action taken against it.
        Assert.NotEqual(default, booking.Id.Value);
    }

    // ---- Done-when: a chat operator's authority is proven both ways ----

    [Fact]
    public async Task AnInvitedThenLinkedOperator_CanCancelABookingTheyAreGrantedFor_AndIsRefusedWithoutTheRole()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        var dispatcherRoleId = await CreatedIdAsync(
            "/api/v1/console/roles",
            new CreateRoleRequest("Dispatcher", [Permission.BookingCancel.Value]),
            seed,
            "roleId");

        var invitedId = await CreatedIdAsync(
            "/api/v1/console/operators",
            new InviteOperatorRequest("Robin", "robin@example.com"),
            seed,
            "operatorId");
        var subject = $"kc-robin-{CalendarSeed.NewId():N}";

        // Bookings to act on, one per direction of the test.
        var bookingWithoutRole = await APendingBookingAsync(seed);

        // First contact (no role granted yet): refused, and the sign-in still links the identity -
        // linking and permission are separate questions, and this proves the ADR's claim that the
        // refusal is a permission refusal, not an identity one.
        using var refused = new HttpRequestMessage(
            HttpMethod.Post, $"/api/v1/console/bookings/{bookingWithoutRole.Id.Value}/cancel");
        refused.Headers.Add(ConsoleApiFactory.SubjectHeader, subject);
        refused.Headers.Add(HeaderSubjectAuthenticationHandler.EmailHeader, "robin@example.com");
        var refusedResponse = await _client.SendAsync(refused);
        Assert.Equal(HttpStatusCode.Forbidden, refusedResponse.StatusCode);

        await using (var db = fixture.CreateDbContext())
        {
            var linkedButPowerless = await db.Operators.FirstAsync(o => o.Id == new OperatorId(invitedId));
            Assert.Equal(subject, linkedButPowerless.ExternalSubjectId);
        }

        // Grant the role, then the identical action succeeds.
        await PostAsync($"/api/v1/console/operators/{invitedId}/roles/{dispatcherRoleId}", null, seed);

        var bookingWithRole = await APendingBookingAsync(seed);
        using var allowed = new HttpRequestMessage(
            HttpMethod.Post, $"/api/v1/console/bookings/{bookingWithRole.Id.Value}/cancel");
        allowed.Headers.Add(ConsoleApiFactory.SubjectHeader, subject);
        var allowedResponse = await _client.SendAsync(allowed);
        Assert.Equal(HttpStatusCode.NoContent, allowedResponse.StatusCode);

        await using var after = fixture.CreateDbContext();
        var cancelled = await after.Events.FirstAsync(e => e.Id == bookingWithRole.Id);
        Assert.Equal(EventStatus.Cancelled, cancelled.Status);
    }

    // ---- Done-when: the email fallback links exactly once, only for an invited row ----

    [Fact]
    public async Task EmailFallback_TwoInvitedRowsSharingAnAddress_LinksNeither()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        var firstId = await CreatedIdAsync(
            "/api/v1/console/operators", new InviteOperatorRequest("Robin", "shared@example.com"), seed, "operatorId");
        var secondId = await CreatedIdAsync(
            "/api/v1/console/operators", new InviteOperatorRequest("Robin Two", "shared@example.com"), seed, "operatorId");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/console/pending-bookings");
        request.Headers.Add(ConsoleApiFactory.SubjectHeader, $"kc-newcomer-{CalendarSeed.NewId():N}");
        request.Headers.Add(HeaderSubjectAuthenticationHandler.EmailHeader, "shared@example.com");
        var response = await _client.SendAsync(request);

        // Refused - the ambiguous match resolves nothing rather than guessing.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        await using var db = fixture.CreateDbContext();
        Assert.Null((await db.Operators.FirstAsync(o => o.Id == new OperatorId(firstId))).ExternalSubjectId);
        Assert.Null((await db.Operators.FirstAsync(o => o.Id == new OperatorId(secondId))).ExternalSubjectId);
    }

    [Fact]
    public async Task EmailFallback_AnAddressThatMatchesAnAlreadyActiveOperatorsEmail_NeverRelinksThatOperator()
    {
        // adr/0088's own Done-when, the second collision it names explicitly: operator X was invited
        // under an address, signed in, is now Active - and still carries that same InvitedEmail
        // (never cleared, see Operator.InvitedEmail's own remarks). A second, still-invited operator Y
        // is later invited under the *same* address. A brand-new subject presenting that email must
        // resolve to Y - the only actual candidate, since the fallback query filters to
        // ExternalSubjectId == null - and must never touch X.
        var seed = await CalendarSeed.WriteAsync(fixture);

        var operatorXId = await CreatedIdAsync(
            "/api/v1/console/operators", new InviteOperatorRequest("Operator X", "reused@example.com"), seed, "operatorId");
        // Granted before the first sign-in - the item's own "roles before first sign-in" case, and
        // what makes X's 200 below actually mean "resolved and permitted" rather than something a
        // permission gap could also explain.
        await PostAsync($"/api/v1/console/operators/{operatorXId}/roles/{seed.Role.Id.Value}", null, seed);
        var subjectX = $"kc-x-{CalendarSeed.NewId():N}";
        using (var xSignsIn = new HttpRequestMessage(HttpMethod.Get, "/api/v1/console/pending-bookings"))
        {
            xSignsIn.Headers.Add(ConsoleApiFactory.SubjectHeader, subjectX);
            xSignsIn.Headers.Add(HeaderSubjectAuthenticationHandler.EmailHeader, "reused@example.com");
            Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(xSignsIn)).StatusCode);
        }

        var operatorYId = await CreatedIdAsync(
            "/api/v1/console/operators", new InviteOperatorRequest("Operator Y", "reused@example.com"), seed, "operatorId");

        using var newcomer = new HttpRequestMessage(HttpMethod.Get, "/api/v1/console/pending-bookings");
        newcomer.Headers.Add(ConsoleApiFactory.SubjectHeader, $"kc-y-{CalendarSeed.NewId():N}");
        newcomer.Headers.Add(HeaderSubjectAuthenticationHandler.EmailHeader, "reused@example.com");
        var response = await _client.SendAsync(newcomer);

        // Y is the only invited candidate (X is excluded by ExternalSubjectId != null) - unambiguous,
        // so it links. Still 403, because Y (unlike X) was never granted a permission - the fallback
        // resolved Y's identity and PermissionChecker separately refused the read; the database below
        // is the actual proof that linking happened despite the HTTP status.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        await using var db = fixture.CreateDbContext();
        var operatorX = await db.Operators.FirstAsync(o => o.Id == new OperatorId(operatorXId));
        Assert.Equal(subjectX, operatorX.ExternalSubjectId);

        var operatorY = await db.Operators.FirstAsync(o => o.Id == new OperatorId(operatorYId));
        Assert.NotNull(operatorY.ExternalSubjectId);
        Assert.NotEqual(subjectX, operatorY.ExternalSubjectId);
    }

    [Fact]
    public async Task ASubjectAlreadyBoundToOneOperator_IsNeverReboundToAnotherByAnEmailCollision()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);

        var operatorAId = await CreatedIdAsync(
            "/api/v1/console/operators", new InviteOperatorRequest("Operator A", "a@example.com"), seed, "operatorId");
        await PostAsync($"/api/v1/console/operators/{operatorAId}/roles/{seed.Role.Id.Value}", null, seed);
        var subjectA = $"kc-a-{CalendarSeed.NewId():N}";

        // A signs in for real once - links.
        using var firstSignIn = new HttpRequestMessage(HttpMethod.Get, "/api/v1/console/pending-bookings");
        firstSignIn.Headers.Add(ConsoleApiFactory.SubjectHeader, subjectA);
        firstSignIn.Headers.Add(HeaderSubjectAuthenticationHandler.EmailHeader, "a@example.com");
        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(firstSignIn)).StatusCode);

        // A second operator is invited afterwards, coincidentally under an email claim A's *token*
        // could carry (e.g. a shared support inbox) - what matters is A's own subject is already
        // resolvable, so the fallback path is never even reached for A again.
        var operatorBId = await CreatedIdAsync(
            "/api/v1/console/operators", new InviteOperatorRequest("Operator B", "b@example.com"), seed, "operatorId");

        using var secondRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/console/pending-bookings");
        secondRequest.Headers.Add(ConsoleApiFactory.SubjectHeader, subjectA);
        secondRequest.Headers.Add(HeaderSubjectAuthenticationHandler.EmailHeader, "b@example.com");
        var secondResponse = await _client.SendAsync(secondRequest);

        // Still resolves as A (200, the same permitted read) - the email claim on this request is
        // irrelevant once the subject already resolves directly.
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);

        await using var db = fixture.CreateDbContext();
        var operatorA = await db.Operators.FirstAsync(o => o.Id == new OperatorId(operatorAId));
        Assert.Equal(subjectA, operatorA.ExternalSubjectId);

        // B is completely untouched - still invited, still unlinked.
        var operatorB = await db.Operators.FirstAsync(o => o.Id == new OperatorId(operatorBId));
        Assert.Null(operatorB.ExternalSubjectId);
    }

    private static int _bookingOffsetDays;

    /// <summary>Each call claims a distinct slot for the seeded worker - <c>ex_events_worker_no_overlap</c>
    /// is real, and a test that calls this twice for the same worker needs two different windows, the
    /// same reason <c>CalendarSeed.WriteAsync</c>'s own callers space bookings apart.</summary>
    private async Task<Event> APendingBookingAsync(SeededTenant seed)
    {
        var startsAt = DateTimeOffset.UtcNow.AddDays(11 + ++_bookingOffsetDays);
        var slot = Event.Materialize(
            new EventId(CalendarSeed.NewId()), seed.Tenant.Id, seed.Calendar.Id, seed.Worker.Id,
            new TimeSlot(startsAt, startsAt.AddMinutes(45)), DateOnly.FromDateTime(startsAt.UtcDateTime),
            DateTimeOffset.UtcNow);
        slot.Claim(seed.Customer.Id, seed.Service.Id, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(2));
        slot.ClearDomainEvents();

        await using var db = fixture.CreateDbContext();
        db.Events.Add(slot);
        await db.SaveChangesAsync();
        return slot;
    }

    private async Task<Guid> CreatedIdAsync(string url, object content, SeededTenant seed, string field)
    {
        var response = await PostAsync(url, content, seed);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<Dictionary<string, Guid>>();
        return payload![field];
    }

    private async Task<T> GetAsync<T>(string url, SeededTenant seed)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(ConsoleApiFactory.SubjectHeader, seed.Operator.ExternalSubjectId);
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private Task<HttpResponseMessage> PostAsync(string url, object? content, SeededTenant seed) =>
        SendAsync(HttpMethod.Post, url, content, seed);

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string url, object? content, SeededTenant seed)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Add(ConsoleApiFactory.SubjectHeader, seed.Operator.ExternalSubjectId);
        if (content is not null)
        {
            request.Content = JsonContent.Create(content, content.GetType());
        }

        return await _client.SendAsync(request);
    }
}
