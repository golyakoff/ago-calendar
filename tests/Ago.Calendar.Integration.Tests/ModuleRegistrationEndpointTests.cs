using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ago.Calendar.Contracts;
using Ago.Calendar.Domain;
using static Ago.Calendar.Integration.Tests.CalendarApiFactory;

namespace Ago.Calendar.Integration.Tests;

/// <summary>
/// `22-11`: the generic provisioning surface, over real HTTP, a real Postgres and the real host - the
/// item's own central claim proven end to end: <b>a chat-originated module call that failed before a
/// registration existed succeeds after one was provisioned through this real path, with no row
/// inserted by hand</b> (unlike <see cref="ChatModuleTaskEndpointTests"/>'s own
/// <c>RegisterChatModuleAsync</c>, which seeds a row directly through the repository because no
/// provisioning endpoint existed yet when that suite was written - this suite is what closes that
/// gap). Revocation and rotation get the identical fails-before/succeeds-after treatment.
/// </summary>
[Collection(PostgresCollection.Name)]
public class ModuleRegistrationEndpointTests(PostgresFixture fixture) : IAsyncLifetime
{
    private CalendarApiFactory _factory = null!;
    private HttpClient _client = null!;
    private SeededTenant _seed = null!;

    private const string ProvisioningSecretHeaderName = "X-Ago-Module-Provisioning-Secret";
    private const string CredentialHeaderName = "X-Ago-Module-Credential";

    public async Task InitializeAsync()
    {
        _seed = await CalendarSeed.WriteAsync(fixture, publicKey: $"modreg-{CalendarSeed.NewId():N}"[..24]);
        _factory = new CalendarApiFactory(fixture);
        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    // ------------------------------------------------------------------------------------------
    // The item's own central claim: register, then a call that failed before succeeds after -
    // through the real endpoint, never a hand-inserted row.
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task AModuleTaskCall_FailsBeforeRegistration_AndSucceedsAfterItIsProvisionedThroughTheRealEndpoint()
    {
        const string credential = "a-freshly-provisioned-secret-of-sufficient-length";

        // Before: no ChatModuleRegistration row exists for this tenant at all - nothing outside a
        // test (this one included, deliberately not seeding anything for this tenant) has ever
        // written one. The real chat-originated call this deployment would answer is refused.
        var before = await StartModuleTaskAsync(_seed.Tenant.Id.Value, credential);
        Assert.Equal(HttpStatusCode.Unauthorized, before.StatusCode);

        // The real path: PUT .../module-registrations/{tenantId}, authenticated by the provisioning
        // secret - not a direct repository write, not a hand-inserted row in either database.
        var registerResponse = await RegisterAsync(_seed.Tenant.Id.Value, credential);
        Assert.Equal(HttpStatusCode.OK, registerResponse.StatusCode);

        // After: the identical call, signed with the credential that registration just accepted,
        // now succeeds.
        var after = await StartModuleTaskAsync(_seed.Tenant.Id.Value, credential);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
    }

    /// <summary>
    /// `22-17`: this item's own opening scenario, proven end to end against a real Postgres - the
    /// platform owner (or a first-time self-service enable) grants a module to a tenant this product
    /// has never heard of, and the module call that failed before now succeeds through the same real
    /// path, with no row inserted by hand on either side. Before this item this test named the
    /// opposite behaviour (<c>Returns404_AndWritesNothing</c>) - see this item's own report for the
    /// captured pre-change 404 and its "tenant_not_found" body, which is exactly what made a first
    /// grant to any new tenant impossible in Production (<c>DevProvisioningEndpoints</c> is not
    /// mapped there).
    ///
    /// <para><b>"Succeeds" here means the credential now authenticates and reaches real business
    /// logic - not that a booking can complete.</b> A freshly auto-provisioned tenant has no published
    /// calendar yet (a separate, later step this product's own console does - see
    /// <c>CalendarSeed.WriteAsync</c>'s own seeding for what a fully configured tenant additionally
    /// needs), so <c>StartModuleTaskHandler</c> correctly answers <c>chat_module_task.not_configured</c>
    /// (404) rather than starting a task. The proof this test makes is narrower and sharper: before
    /// registration the call is refused at the credential-verification layer
    /// (<see cref="HttpStatusCode.Unauthorized"/> - the module never even looks at whether a
    /// calendar exists); after registration the identical call is <b>authenticated</b> and reaches
    /// the tenant-configuration check instead, which is the boundary this item actually
    /// owns.</para>
    /// </summary>
    [Fact]
    public async Task Register_ForATenantThatDoesNotExist_ProvisionsTheTenant_AndTheCredentialStartsAuthenticating()
    {
        var newTenantId = Guid.NewGuid();
        const string credential = "a-secret-for-a-tenant-that-did-not-exist-yet";

        // Before: no Tenant row and no ChatModuleRegistration row - refused at authentication, before
        // any business rule about calendars is ever consulted.
        var before = await StartModuleTaskAsync(newTenantId, credential);
        Assert.Equal(HttpStatusCode.Unauthorized, before.StatusCode);

        var response = await RegisterAsync(newTenantId, credential, displayName: "A Brand New Prospect");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // After: the identical call, against a tenant that did not exist a moment ago, is no longer
        // refused for lacking a valid credential - it reaches StartModuleTaskHandler and is refused
        // for the real, separate, and expected reason a brand-new tenant has: no calendar published
        // yet.
        var after = await StartModuleTaskAsync(newTenantId, credential);
        Assert.Equal(HttpStatusCode.NotFound, after.StatusCode);
        Assert.Contains("chat_module_task.not_configured", await after.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    /// <summary>A second register for the same never-seen-before tenant id is a real conflict
    /// (`AlreadyRegistered`), not a repeat provisioning - the auto-provisioned tenant is not exempt
    /// from the ordinary "register is create-only" rule.</summary>
    [Fact]
    public async Task Register_TwiceForATenantThatDidNotExist_Returns409OnTheSecondCall()
    {
        var newTenantId = Guid.NewGuid();
        await RegisterAsync(newTenantId, "the-first-registration-secret-of-enough-length");

        var second = await RegisterAsync(newTenantId, "a-different-secret-nobody-should-accept-x");

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Register_ForATenantAlreadyRegistered_Returns409_AndDoesNotDisturbTheExistingRow()
    {
        const string original = "the-tenants-original-secret-of-enough-length";
        await RegisterAsync(_seed.Tenant.Id.Value, original);

        var second = await RegisterAsync(_seed.Tenant.Id.Value, "a-different-secret-nobody-should-accept");
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        // The original credential still verifies - the conflicting second attempt changed nothing.
        var stillWorks = await StartModuleTaskAsync(_seed.Tenant.Id.Value, original);
        Assert.Equal(HttpStatusCode.OK, stillWorks.StatusCode);
    }

    [Fact]
    public async Task Register_WithAWrongProvisioningSecret_IsRefused_AndWritesNothing()
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/module-registrations/{_seed.Tenant.Id.Value}")
        {
            Content = JsonContent.Create(new { credential = "a-perfectly-valid-shaped-secret-value" }),
        };
        request.Headers.Add(ProvisioningSecretHeaderName, "not-the-configured-provisioning-secret");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var status = await GetStatusAsync(_seed.Tenant.Id.Value);
        Assert.False(status.Exists);
    }

    [Fact]
    public async Task Register_WithNoProvisioningSecretHeaderAtAll_IsRefused()
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/module-registrations/{_seed.Tenant.Id.Value}")
        {
            Content = JsonContent.Create(new { credential = "a-perfectly-valid-shaped-secret-value" }),
        };

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>`20-24`'s own lesson, restated: a 401 that looks like a missing route proves nothing.
    /// This nonsense sibling path proves the real route is genuinely mapped and genuinely refusing,
    /// not merely unreachable.</summary>
    [Fact]
    public async Task AnUnmappedSiblingRoute_Returns404()
    {
        var response = await _client.GetAsync("/api/v1/module-registrations-nonexistent-sibling-route");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ------------------------------------------------------------------------------------------
    // Rotation: no downtime for the site being rotated, and no effect on any other site.
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Rotate_TheOldCredential_StillVerifiesImmediatelyAfterRotation()
    {
        const string original = "the-original-secret-before-any-rotation-x";
        const string rotated = "the-brand-new-secret-installed-by-rotation";
        await RegisterAsync(_seed.Tenant.Id.Value, original);

        var rotateResponse = await RotateAsync(_seed.Tenant.Id.Value, rotated);
        Assert.Equal(HttpStatusCode.OK, rotateResponse.StatusCode);

        // The claim the item's own Done-when makes: rotating does not break a call signed a moment
        // ago with the credential that was, until this call, the only one.
        var withOldCredential = await StartModuleTaskAsync(_seed.Tenant.Id.Value, original);
        Assert.Equal(HttpStatusCode.OK, withOldCredential.StatusCode);

        var withNewCredential = await StartModuleTaskAsync(_seed.Tenant.Id.Value, rotated);
        Assert.Equal(HttpStatusCode.OK, withNewCredential.StatusCode);
    }

    /// <summary>The Done-when's own exact wording: rotating <b>one</b> site does not cost
    /// <b>another</b> site anything - proven with two real, independently registered tenants.</summary>
    [Fact]
    public async Task Rotate_OneTenantsCredential_DoesNotAffectAnotherTenantsRegistration()
    {
        var otherSeed = await CalendarSeed.WriteAsync(fixture, publicKey: $"modreg2-{CalendarSeed.NewId():N}"[..24]);
        const string thisTenantOriginal = "this-tenants-secret-before-any-rotation-x";
        const string thisTenantRotated = "this-tenants-secret-after-being-rotated-x";
        const string otherTenantSecret = "the-other-tenants-own-untouched-secret-x";
        await RegisterAsync(_seed.Tenant.Id.Value, thisTenantOriginal);
        await RegisterAsync(otherSeed.Tenant.Id.Value, otherTenantSecret);

        var rotateResponse = await RotateAsync(_seed.Tenant.Id.Value, thisTenantRotated);
        Assert.Equal(HttpStatusCode.OK, rotateResponse.StatusCode);

        // The other tenant's own credential, never touched by this rotation, still works - proving
        // "without downtime for other sites" on real, independent rows.
        var otherStillWorks = await StartModuleTaskAsync(otherSeed.Tenant.Id.Value, otherTenantSecret);
        Assert.Equal(HttpStatusCode.OK, otherStillWorks.StatusCode);
    }

    [Fact]
    public async Task Rotate_ForATenantWithNoRegistration_Returns404()
    {
        var response = await RotateAsync(Guid.NewGuid(), "a-secret-for-a-tenant-nobody-ever-registered");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ------------------------------------------------------------------------------------------
    // Revocation: the item's own third Done-when, proven by trying the call.
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task ACallThatSucceeded_ThenARevoke_ThenTheSameCall_IsRefused()
    {
        const string credential = "a-secret-that-will-shortly-be-revoked-xxx";
        await RegisterAsync(_seed.Tenant.Id.Value, credential);

        var beforeRevoke = await StartModuleTaskAsync(_seed.Tenant.Id.Value, credential);
        Assert.Equal(HttpStatusCode.OK, beforeRevoke.StatusCode);

        var revokeResponse = await RevokeAsync(_seed.Tenant.Id.Value);
        Assert.Equal(HttpStatusCode.OK, revokeResponse.StatusCode);

        var afterRevoke = await StartModuleTaskAsync(_seed.Tenant.Id.Value, credential);
        Assert.Equal(HttpStatusCode.Unauthorized, afterRevoke.StatusCode);
    }

    [Fact]
    public async Task Revoke_ForATenantWithNoRegistration_Returns404()
    {
        var response = await RevokeAsync(Guid.NewGuid());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ------------------------------------------------------------------------------------------
    // The item's own fourth Done-when: a registration that exists on one side only is detectable -
    // this module's own half of that check.
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetStatus_ForATenantWithNoRegistration_ReportsNotExists()
    {
        var status = await GetStatusAsync(_seed.Tenant.Id.Value);

        Assert.False(status.Exists);
    }

    [Fact]
    public async Task GetStatus_AfterRegistering_ReportsExists()
    {
        await RegisterAsync(_seed.Tenant.Id.Value, "a-secret-for-the-status-check-of-enough-length");

        var status = await GetStatusAsync(_seed.Tenant.Id.Value);

        Assert.True(status.Exists);
    }

    [Fact]
    public async Task GetStatus_AfterRevoking_ReportsNotExistsAgain()
    {
        await RegisterAsync(_seed.Tenant.Id.Value, "a-secret-for-the-status-check-of-enough-length");
        await RevokeAsync(_seed.Tenant.Id.Value);

        var status = await GetStatusAsync(_seed.Tenant.Id.Value);

        Assert.False(status.Exists);
    }

    [Fact]
    public async Task GetStatus_WithAWrongProvisioningSecret_IsRefused()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/module-registrations/{_seed.Tenant.Id.Value}");
        request.Headers.Add(ProvisioningSecretHeaderName, "not-the-configured-provisioning-secret");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ------------------------------------------------------------------------------------------

    private async Task<HttpResponseMessage> RegisterAsync(Guid tenantId, string credential, string? displayName = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/module-registrations/{tenantId}")
        {
            Content = JsonContent.Create(new { credential, displayName }),
        };
        request.Headers.Add(ProvisioningSecretHeaderName, TestProvisioningSecret);
        return await _client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> RotateAsync(Guid tenantId, string newCredential)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/module-registrations/{tenantId}/rotate")
        {
            Content = JsonContent.Create(new { newCredential }),
        };
        request.Headers.Add(ProvisioningSecretHeaderName, TestProvisioningSecret);
        return await _client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> RevokeAsync(Guid tenantId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/module-registrations/{tenantId}");
        request.Headers.Add(ProvisioningSecretHeaderName, TestProvisioningSecret);
        return await _client.SendAsync(request);
    }

    private async Task<StatusResponse> GetStatusAsync(Guid tenantId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/module-registrations/{tenantId}");
        request.Headers.Add(ProvisioningSecretHeaderName, TestProvisioningSecret);
        var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<StatusResponse>())!;
    }

    /// <summary>Starts a module task exactly as `Ago.Chat.*`'s own real <c>HttpModuleGateway</c>
    /// would - the wire contract's other route, reused here (rather than a bare Postgres read) because
    /// the item's own central claim is about a real, refusable/answerable call, not about a row's mere
    /// existence.</summary>
    private async Task<HttpResponseMessage> StartModuleTaskAsync(Guid tenantId, string credential)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/module-tasks")
        {
            Content = JsonContent.Create(new ModuleTaskStartRequest(Guid.NewGuid(), tenantId, Guid.NewGuid(), "/booking")),
        };
        request.Headers.Add(CredentialHeaderName, MintCredentialHeader(tenantId, credential));
        return await _client.SendAsync(request);
    }

    /// <summary>This suite's own independent re-derivation of the wire format
    /// <c>HmacModuleCallCredentialValidator</c> checks - the identical approach
    /// <c>ChatModuleTaskEndpointTests.MintCredentialHeader</c> already takes, and for the same reason
    /// (see that method's own remarks): duplicated rather than shared, so this test would catch the
    /// validator disagreeing with its own documented contract.</summary>
    private static string MintCredentialHeader(Guid siteId, string secret)
    {
        var now = DateTimeOffset.UtcNow;
        var payloadJson = JsonSerializer.Serialize(
            new TestPayload(siteId, now.ToUnixTimeSeconds(), now.AddSeconds(60).ToUnixTimeSeconds()),
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

    private sealed record StatusResponse(bool Exists, DateTimeOffset? RegisteredAt, bool HasCredentialInGracePeriod);
}
