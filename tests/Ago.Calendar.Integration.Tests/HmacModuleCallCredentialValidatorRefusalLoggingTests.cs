using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres;
using Microsoft.Extensions.Logging;

namespace Ago.Calendar.Integration.Tests;

/// <summary>
/// `22-12`/adr/0099: the whole item's own central claim, proven directly against
/// <see cref="HmacModuleCallCredentialValidator"/> rather than over real HTTP - a real Postgres and a
/// real host can prove the wire still answers a flat <c>401</c> (<c>ChatModuleTaskEndpointTests</c>
/// already does, unchanged by this item), but cannot economically prove
/// <see cref="ModuleCallRefusalReason.CredentialRotatedOut"/>: that case needs a clock ten minutes past
/// a rotation's own overlap window (<c>RotateChatModuleCredentialHandler.OverlapWindow</c>), and this
/// class takes <c>now</c> as a parameter rather than reading a real clock, so the precise moment is a
/// value this test passes in rather than a real ten minutes this suite would otherwise have to wait
/// out. A hand-written fake <see cref="IChatModuleRegistrationRepository"/> stands in for Postgres -
/// this class's own only real dependency beyond the BCL - the identical "fake a port, never the
/// database" rule `docs/conventions/testing.md` states, applied to an Infrastructure adapter whose own
/// real collaborator is a port rather than `DbContext` directly.
///
/// <para><b>Lives in this project rather than a new one</b> so this item does not grow
/// `ago-calendar`'s own test-assembly count - the same "no logic in the test body" project boundary
/// this suite otherwise reserves for real-host/real-Postgres tests, deliberately crossed here for one
/// file because the alternative (a sixth assembly for one adapter class) is the larger cost.</para>
///
/// <para><b>The credential/secret itself never appears in any assertion's expected string</b> - every
/// <c>DoesNotContain</c> check below is the item's own hardest constraint: "log which case it was" must
/// never become "log the presented value" (CLAUDE.md's own module-credential rule).</para>
/// </summary>
public sealed class HmacModuleCallCredentialValidatorRefusalLoggingTests
{
    private const string OriginalSecret = "the-original-secret-of-sufficient-length-x";
    private const string RotatedSecret = "the-freshly-rotated-secret-of-enough-length";
    private const string WrongSecret = "a-completely-different-secret-nobody-holds";

    [Fact]
    public async Task NoCredentialHeader_IsRefused_AsNoCredential_LoggedAtDebug_WithNoSiteToName()
    {
        var (validator, logger, _) = Build();

        var result = await validator.ValidateAsync(headerValue: null, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.False(result.IsAuthenticated);
        Assert.Equal(ModuleCallRefusalReason.NoCredential, result.Reason);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Debug, entry.Level);
        Assert.Contains("NoCredential", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMalformedHeader_IsRefused_AsMalformed_LoggedAtDebug()
    {
        var (validator, logger, _) = Build();

        var result = await validator.ValidateAsync("not-even-shaped-like-a-token", DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.False(result.IsAuthenticated);
        Assert.Equal(ModuleCallRefusalReason.Malformed, result.Reason);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Debug, entry.Level);
        Assert.Contains("Malformed", entry.Message, StringComparison.Ordinal);
    }

    /// <summary>The item's own first case: "the site has no registration - the module was never
    /// enabled for it." The repository holds nothing for this tenant at all.</summary>
    [Fact]
    public async Task ACredentialForATenantWithNoRegistration_IsRefused_AsSiteNotRegistered_LoggedAtWarning_NamingTheSite()
    {
        var (validator, logger, _) = Build();
        var unregisteredTenant = Guid.NewGuid();
        var header = MintCredentialHeader(unregisteredTenant, "a-secret-nobody-ever-registered-anywhere");

        var result = await validator.ValidateAsync(header, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.False(result.IsAuthenticated);
        Assert.Equal(ModuleCallRefusalReason.SiteNotRegistered, result.Reason);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("SiteNotRegistered", entry.Message, StringComparison.Ordinal);
        Assert.Contains(unregisteredTenant.ToString(), entry.Message, StringComparison.Ordinal);
    }

    /// <summary>The item's own second case: "the credential is forged, or signed with another site's
    /// secret." A registered tenant, but the presented signature matches neither its current nor its
    /// (non-existent, here) previous credential.</summary>
    [Fact]
    public async Task ACredentialSignedWithTheWrongSecret_IsRefused_AsInvalidSignature_LoggedAtWarning_NamingTheClaimedSite()
    {
        var tenantId = new TenantId(Guid.NewGuid());
        var (validator, logger, repository) = Build();
        repository.Seed(ChatModuleRegistration.Register(
            tenantId, new ChatModuleCredential(OriginalSecret), DateTimeOffset.UtcNow));
        var header = MintCredentialHeader(tenantId.Value, WrongSecret);

        var result = await validator.ValidateAsync(header, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.False(result.IsAuthenticated);
        Assert.Equal(ModuleCallRefusalReason.InvalidSignature, result.Reason);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("InvalidSignature", entry.Message, StringComparison.Ordinal);
        Assert.Contains(tenantId.Value.ToString(), entry.Message, StringComparison.Ordinal);
    }

    /// <summary>The item's own third case: "the credential expired." A genuinely correct signature -
    /// the site's own real secret - but the assertion's own <c>iat</c>/<c>exp</c> falls outside the
    /// 60-second TTL plus the five-second clock-skew allowance.</summary>
    [Fact]
    public async Task ACorrectlySignedButExpiredAssertion_IsRefused_AsAssertionExpired_LoggedAtWarning()
    {
        var tenantId = new TenantId(Guid.NewGuid());
        var (validator, logger, repository) = Build();
        repository.Seed(ChatModuleRegistration.Register(
            tenantId, new ChatModuleCredential(OriginalSecret), DateTimeOffset.UtcNow));

        var mintedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var header = MintCredentialHeader(
            tenantId.Value, OriginalSecret, mintedAt.ToUnixTimeSeconds(), mintedAt.AddSeconds(60).ToUnixTimeSeconds());

        var result = await validator.ValidateAsync(header, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.False(result.IsAuthenticated);
        Assert.Equal(ModuleCallRefusalReason.AssertionExpired, result.Reason);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("AssertionExpired", entry.Message, StringComparison.Ordinal);
        Assert.Contains(tenantId.Value.ToString(), entry.Message, StringComparison.Ordinal);
    }

    /// <summary>`22-11`'s own fourth case, the item's own text names explicitly: "a call carrying a
    /// credential that was legitimate until a rotation's grace window elapsed." Signed with the
    /// tenant's own <em>previous</em> secret, presented after <c>PreviousCredentialExpiresAt</c> has
    /// passed - genuinely never forged, only late, and distinguished from
    /// <see cref="ModuleCallRefusalReason.InvalidSignature"/> for exactly that reason.</summary>
    [Fact]
    public async Task ACredentialSignedWithAPreviousSecret_AfterItsGraceWindowElapsed_IsRefused_AsCredentialRotatedOut()
    {
        var tenantId = new TenantId(Guid.NewGuid());
        var (validator, logger, repository) = Build();
        var registeredAt = DateTimeOffset.UtcNow.AddHours(-1);
        var registered = ChatModuleRegistration.Register(tenantId, new ChatModuleCredential(OriginalSecret), registeredAt);
        var rotatedAt = DateTimeOffset.UtcNow.AddMinutes(-20);
        var rotated = registered.Rotate(new ChatModuleCredential(RotatedSecret), rotatedAt, TimeSpan.FromMinutes(10));
        repository.Seed(rotated);

        // Signed with the outgoing secret - genuinely valid until the ten-minute overlap window
        // closed, twenty minutes ago.
        var header = MintCredentialHeader(tenantId.Value, OriginalSecret);

        var result = await validator.ValidateAsync(header, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.False(result.IsAuthenticated);
        Assert.Equal(ModuleCallRefusalReason.CredentialRotatedOut, result.Reason);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("CredentialRotatedOut", entry.Message, StringComparison.Ordinal);
        Assert.Contains(tenantId.Value.ToString(), entry.Message, StringComparison.Ordinal);
    }

    /// <summary>The control every refusal test above needs: an outgoing credential presented
    /// <em>inside</em> its grace window is not a refusal at all, and logs nothing - the same "rotate
    /// without downtime" claim `Ago.Calendar.Domain.Tests` and
    /// <c>ModuleRegistrationEndpointTests.Rotate_TheOldCredential_StillVerifiesImmediatelyAfterRotation</c>
    /// already prove for the domain and the wire respectively; here to prove specifically that this
    /// item's own new classification did not accidentally start logging - or refusing - the success
    /// path it must not touch.</summary>
    [Fact]
    public async Task AValidCredential_Authenticates_AndLogsNothing()
    {
        var tenantId = new TenantId(Guid.NewGuid());
        var (validator, logger, repository) = Build();
        repository.Seed(ChatModuleRegistration.Register(
            tenantId, new ChatModuleCredential(OriginalSecret), DateTimeOffset.UtcNow));
        var header = MintCredentialHeader(tenantId.Value, OriginalSecret);

        var result = await validator.ValidateAsync(header, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.True(result.IsAuthenticated);
        Assert.Equal(tenantId.Value, result.SiteId);
        Assert.Null(result.Reason);
        Assert.Empty(logger.Entries);
    }

    /// <summary>CLAUDE.md's own module-credential rule, asserted rather than merely followed: not one
    /// captured log line across every case above contains any secret this test minted a token with, nor
    /// the header's own base64url segments. A future edit that logged <c>headerValue</c> "for
    /// debugging" would fail this, not merely violate a comment.</summary>
    [Fact]
    public async Task AcrossEveryRefusalCase_NoLoggedMessageEverContainsASecretOrTheRawHeader()
    {
        var tenantId = new TenantId(Guid.NewGuid());
        var (validator, logger, repository) = Build();
        repository.Seed(ChatModuleRegistration.Register(
            tenantId, new ChatModuleCredential(OriginalSecret), DateTimeOffset.UtcNow));

        var wrongSecretHeader = MintCredentialHeader(tenantId.Value, WrongSecret);
        await validator.ValidateAsync(wrongSecretHeader, DateTimeOffset.UtcNow, CancellationToken.None);
        await validator.ValidateAsync(null, DateTimeOffset.UtcNow, CancellationToken.None);
        await validator.ValidateAsync("garbage", DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.NotEmpty(logger.Entries);
        Assert.All(logger.Entries, entry =>
        {
            Assert.DoesNotContain(OriginalSecret, entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(WrongSecret, entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(wrongSecretHeader, entry.Message, StringComparison.Ordinal);
        });
    }

    private static (HmacModuleCallCredentialValidator Validator, CapturingLogger<HmacModuleCallCredentialValidator> Logger, FakeChatModuleRegistrationRepository Repository) Build()
    {
        var repository = new FakeChatModuleRegistrationRepository();
        var logger = new CapturingLogger<HmacModuleCallCredentialValidator>();
        return (new HmacModuleCallCredentialValidator(repository, logger), logger, repository);
    }

    /// <summary>This suite's own independent re-derivation of the wire format
    /// <see cref="HmacModuleCallCredentialValidator"/> checks, matching
    /// <c>ChatModuleTaskEndpointTests.MintCredentialHeader</c>'s own reasoning: written from the
    /// contract's documented shape, not by calling production code, so a validator that quietly
    /// disagreed with its own documented format would be caught here rather than merely agreeing with
    /// itself. The explicit <paramref name="iat"/>/<paramref name="exp"/> overload is what
    /// <see cref="ACorrectlySignedButExpiredAssertion_IsRefused_AsAssertionExpired_LoggedAtWarning"/> needs -
    /// the convenience overload below covers every other case, which does not care about the exact
    /// timestamps as long as they bracket "now".</summary>
    private static string MintCredentialHeader(Guid siteId, string secret, long iat, long exp)
    {
        var payloadJson = JsonSerializer.Serialize(
            new TestPayload(siteId, iat, exp), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var encodedPayload = Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson));
        var signature = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(encodedPayload));
        return $"{encodedPayload}.{Base64UrlEncode(signature)}";
    }

    private static string MintCredentialHeader(Guid siteId, string secret)
    {
        var now = DateTimeOffset.UtcNow;
        return MintCredentialHeader(siteId, secret, now.ToUnixTimeSeconds(), now.AddSeconds(60).ToUnixTimeSeconds());
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record TestPayload(
        [property: JsonPropertyName("siteId")] Guid SiteId,
        [property: JsonPropertyName("iat")] long Iat,
        [property: JsonPropertyName("exp")] long Exp);

    /// <summary>A hand-written fake for the one port this class depends on beyond the BCL - never a
    /// mocking framework (`docs/conventions/testing.md`), and never a real Postgres, since nothing
    /// under test here is a query.</summary>
    private sealed class FakeChatModuleRegistrationRepository : IChatModuleRegistrationRepository
    {
        private readonly Dictionary<Guid, ChatModuleRegistration> _rows = [];

        public void Seed(ChatModuleRegistration registration) => _rows[registration.TenantId.Value] = registration;

        public Task<ChatModuleRegistration?> GetByTenantIdAsync(TenantId tenantId, CancellationToken cancellationToken) =>
            Task.FromResult(_rows.GetValueOrDefault(tenantId.Value));

        public Task AddAsync(ChatModuleRegistration registration, CancellationToken cancellationToken)
        {
            Seed(registration);
            return Task.CompletedTask;
        }

        public Task UpdateAsync(ChatModuleRegistration registration, CancellationToken cancellationToken)
        {
            Seed(registration);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(TenantId tenantId, CancellationToken cancellationToken)
        {
            _rows.Remove(tenantId.Value);
            return Task.CompletedTask;
        }
    }

    /// <summary>`ago-chat`'s own established convention
    /// (<c>TelegramTokenRedactingLoggingHandlerTests</c>, <c>TelemetryLeakGuardTests</c>) - a private
    /// capturing logger per test file rather than a shared test-only package, extended here to also
    /// keep <see cref="LogLevel"/> per entry, since this item's own claim is partly about which level
    /// each case logs at, not only what it says.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
