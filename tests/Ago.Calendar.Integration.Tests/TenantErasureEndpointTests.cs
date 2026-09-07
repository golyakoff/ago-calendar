using System.Net;
using System.Net.Http.Json;
using Dapper;
using static Ago.Calendar.Integration.Tests.CalendarApiFactory;

namespace Ago.Calendar.Integration.Tests;

/// <summary>
/// `22-30`: this item's own central claim, on the calendar's own side of the boundary - "the second
/// half is proved rather than assumed." Every assertion here queries the real Postgres this host is
/// pointed at directly, never the endpoint's own response body alone, for the identical
/// "a deletion test that only checks what it remembers to check is exactly how erasure quietly
/// becomes partial" reasoning <c>Ago.Chat.Integration.Tests.SiteErasureIntegrationTests</c>'s own
/// class remarks already state for the sibling product.
///
/// <para><b>Authenticated the way `22-11`'s registration calls already are</b> - the provisioning
/// secret, not a per-site <c>ModuleCallCredential</c> - the deliberate choice
/// <c>ModuleRegistrationEndpoints.HandleEraseAsync</c>'s own remarks explain: it is what lets this
/// endpoint answer for a tenant whose per-site registration was revoked, lapsed, or never
/// provisioned, which is exactly the class of tenant `22-30` exists to still reach.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class TenantErasureEndpointTests(PostgresFixture fixture) : IAsyncLifetime
{
    private CalendarApiFactory _factory = null!;
    private HttpClient _client = null!;

    private const string ProvisioningSecretHeaderName = "X-Ago-Module-Provisioning-Secret";

    public Task InitializeAsync()
    {
        _factory = new CalendarApiFactory(fixture);
        _client = _factory.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    /// <summary>The item's own first Done-when, word for word: "leaves no `tenants`, `customers`,
    /// `events`, `workers` ... row for them, proven by erasing one and querying the calendar's
    /// database" - plus the two tables `adr/0093`'s own migration replaced `operators`/`roles` with
    /// (`role_assignment_projections`) and the registration row `22-11` built
    /// (`chat_module_registrations`), which the backlog's own cascade list names too.</summary>
    [Fact]
    public async Task Erase_ForATenantWithEverything_RemovesEveryTableItHeld_ProvenByQueryingPostgresDirectly()
    {
        var seed = await CalendarSeed.WriteAsync(fixture, publicKey: $"erase-{CalendarSeed.NewId():N}"[..24], workerQuota: 1);

        // A booking (`events`), so the item's own explicitly-named table has a real row to lose - not
        // only the ones CalendarSeed writes for every tenant regardless.
        var eventId = Guid.NewGuid();
        await using (var connection = await fixture.DataSource.OpenConnectionAsync())
        {
            // local_date is computed server-side (@startsAt::date) rather than passed as a DateOnly
            // parameter - Dapper has no built-in DateOnly-to-NpgsqlDbType mapping, and this table's
            // own value is derivable from starts_at anyway.
            await connection.ExecuteAsync(
                """
                insert into events
                    (id, tenant_id, calendar_id, worker_id, service_id, customer_id, status,
                     starts_at, ends_at, local_date, created_at)
                values
                    (@id, @tenantId, @calendarId, @workerId, @serviceId, @customerId, 'Confirmed',
                     @startsAt, @endsAt, (@startsAt at time zone 'utc')::date, @now)
                """,
                new
                {
                    id = eventId,
                    tenantId = seed.Tenant.Id.Value,
                    calendarId = seed.Calendar.Id.Value,
                    workerId = seed.Worker.Id.Value,
                    serviceId = seed.Service.Id.Value,
                    customerId = seed.Customer.Id.Value,
                    startsAt = CalendarSeed.Now,
                    endsAt = CalendarSeed.Now.AddMinutes(45),
                    now = CalendarSeed.Now,
                });
        }

        // A live chat-module registration - `22-11`'s own row, which this item's own scope names as
        // one of the "everything under it" tables a tenant erasure must reach too.
        await RegisterAsync(seed.Tenant.Id.Value, "a-registration-secret-of-enough-length");

        // `23-12`: the second outbox-fed projection table with no foreign key to tenants - found the
        // same way role_assignment_projections was, by proving this test against a real Postgres
        // rather than trusting the schema's own cascades to be complete.
        await using (var connection = await fixture.DataSource.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                "insert into contact_visibility_projections (tenant_id, rung, updated_at) values (@tenantId, 0, @now)",
                new { tenantId = seed.Tenant.Id.Value, now = CalendarSeed.Now });
        }

        Assert.Equal(1, await CountAsync("tenants", "id", seed.Tenant.Id.Value));
        Assert.Equal(1, await CountAsync("customers", "tenant_id", seed.Tenant.Id.Value));
        Assert.Equal(1, await CountAsync("events", "tenant_id", seed.Tenant.Id.Value));
        Assert.Equal(1, await CountAsync("workers", "tenant_id", seed.Tenant.Id.Value));
        Assert.Equal(1, await CountAsync("calendars", "tenant_id", seed.Tenant.Id.Value));
        Assert.Equal(1, await CountAsync("services", "tenant_id", seed.Tenant.Id.Value));
        Assert.Equal(1, await CountAsync("role_assignment_projections", "tenant_id", seed.Tenant.Id.Value));
        Assert.Equal(1, await CountAsync("chat_module_registrations", "tenant_id", seed.Tenant.Id.Value));
        Assert.Equal(1, await CountAsync("contact_visibility_projections", "tenant_id", seed.Tenant.Id.Value));

        var response = await EraseAsync(seed.Tenant.Id.Value);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<TenantErasureResponse>();
        Assert.NotNull(body);
        Assert.True(body.TenantExisted);
        Assert.True(body.Confirmed);

        // The database itself, queried fresh - not the response body, and not the same DbContext
        // the endpoint's own request used (a new connection per CountAsync call below), so this is
        // genuinely a second, independent read of Postgres proving the first one was honest.
        Assert.Equal(0, await CountAsync("tenants", "id", seed.Tenant.Id.Value));
        Assert.Equal(0, await CountAsync("customers", "tenant_id", seed.Tenant.Id.Value));
        Assert.Equal(0, await CountAsync("events", "tenant_id", seed.Tenant.Id.Value));
        Assert.Equal(0, await CountAsync("workers", "tenant_id", seed.Tenant.Id.Value));
        Assert.Equal(0, await CountAsync("calendars", "tenant_id", seed.Tenant.Id.Value));
        Assert.Equal(0, await CountAsync("services", "tenant_id", seed.Tenant.Id.Value));
        Assert.Equal(0, await CountAsync("role_assignment_projections", "tenant_id", seed.Tenant.Id.Value));
        Assert.Equal(0, await CountAsync("chat_module_registrations", "tenant_id", seed.Tenant.Id.Value));
        Assert.Equal(0, await CountAsync("contact_visibility_projections", "tenant_id", seed.Tenant.Id.Value));
    }

    /// <summary>`CLAUDE.md` rule 5, over real HTTP and a real Postgres this time: a second erase call
    /// for a tenant the first call already removed is not an error and does not disturb anything -
    /// the second half of the item's own idempotency requirement, proven at the wire rather than only
    /// at the Application layer (<c>EraseTenantDataHandlerTests</c>'s own fake-backed proof).</summary>
    [Fact]
    public async Task Erase_CalledTwice_IsANoOpTheSecondTime()
    {
        var seed = await CalendarSeed.WriteAsync(fixture, publicKey: $"erase2-{CalendarSeed.NewId():N}"[..24]);

        var first = await EraseAsync(seed.Tenant.Id.Value);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstBody = await first.Content.ReadFromJsonAsync<TenantErasureResponse>();
        Assert.True(firstBody!.TenantExisted);
        Assert.True(firstBody.Confirmed);

        var second = await EraseAsync(seed.Tenant.Id.Value);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var secondBody = await second.Content.ReadFromJsonAsync<TenantErasureResponse>();
        Assert.False(secondBody!.TenantExisted);
        Assert.True(secondBody.Confirmed);

        Assert.Equal(0, await CountAsync("tenants", "id", seed.Tenant.Id.Value));
    }

    /// <summary>A tenant this deployment never heard of - `22-30`'s own lapsed/revoked/never-reached
    /// cases all converge here at the calendar's own boundary: whatever chat's own reason for asking,
    /// "nothing to erase" is success, not a 404.</summary>
    [Fact]
    public async Task Erase_ForATenantThatNeverExisted_IsConfirmedCleanWithoutError()
    {
        var response = await EraseAsync(Guid.NewGuid());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<TenantErasureResponse>();
        Assert.False(body!.TenantExisted);
        Assert.True(body.Confirmed);
    }

    [Fact]
    public async Task Erase_WithAWrongProvisioningSecret_IsRefused_AndErasesNothing()
    {
        var seed = await CalendarSeed.WriteAsync(fixture, publicKey: $"erase3-{CalendarSeed.NewId():N}"[..24]);

        using var request = new HttpRequestMessage(
            HttpMethod.Delete, $"/api/v1/module-registrations/{seed.Tenant.Id.Value}/tenant-data");
        request.Headers.Add(ProvisioningSecretHeaderName, "not-the-configured-provisioning-secret");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(1, await CountAsync("tenants", "id", seed.Tenant.Id.Value));
    }

    [Fact]
    public async Task Erase_WithNoProvisioningSecretHeaderAtAll_IsRefused()
    {
        var seed = await CalendarSeed.WriteAsync(fixture, publicKey: $"erase4-{CalendarSeed.NewId():N}"[..24]);

        using var request = new HttpRequestMessage(
            HttpMethod.Delete, $"/api/v1/module-registrations/{seed.Tenant.Id.Value}/tenant-data");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(1, await CountAsync("tenants", "id", seed.Tenant.Id.Value));
    }

    /// <summary>The item's own "lapsed or revoked" case, at the boundary that matters: a tenant whose
    /// `chat_module_registrations` row is already gone (revoked, in this test's own stand-in) still
    /// has its tenant data erased - this endpoint never looks at that table to decide whether to act,
    /// unlike every other handler in `ModuleRegistrationEndpoints`. See that method's own remarks for
    /// why.</summary>
    [Fact]
    public async Task Erase_ForATenantWithNoChatModuleRegistration_StillErasesTheTenantsOwnData()
    {
        var seed = await CalendarSeed.WriteAsync(fixture, publicKey: $"erase5-{CalendarSeed.NewId():N}"[..24]);
        // Deliberately never registered - the never-provisioned case; the revoked case is identical
        // from this endpoint's own point of view, since revocation is itself just the absence of a
        // chat_module_registrations row (RevokeChatModuleRegistrationHandler's own remarks).
        Assert.Equal(0, await CountAsync("chat_module_registrations", "tenant_id", seed.Tenant.Id.Value));

        var response = await EraseAsync(seed.Tenant.Id.Value);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, await CountAsync("tenants", "id", seed.Tenant.Id.Value));
    }

    // ------------------------------------------------------------------------------------------

    private async Task<HttpResponseMessage> EraseAsync(Guid tenantId)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Delete, $"/api/v1/module-registrations/{tenantId}/tenant-data");
        request.Headers.Add(ProvisioningSecretHeaderName, TestProvisioningSecret);
        return await _client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> RegisterAsync(Guid tenantId, string credential)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/module-registrations/{tenantId}")
        {
            Content = JsonContent.Create(new { credential, displayName = (string?)null }),
        };
        request.Headers.Add(ProvisioningSecretHeaderName, TestProvisioningSecret);
        return await _client.SendAsync(request);
    }

    private async Task<int> CountAsync(string table, string column, Guid id)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<int>(
            $"select count(*) from {table} where {column} = @id", new { id });
    }

    private sealed record TenantErasureResponse(bool TenantExisted, bool Confirmed);
}
