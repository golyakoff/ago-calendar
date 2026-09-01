using System.Security.Cryptography;
using System.Text;
using Ago.Calendar.Application.UseCases.PhoneVerification;
using Ago.Calendar.Domain;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.Tests;

/// <summary>
/// `20-10`'s own Initiate/Confirm handlers, with every port faked - the identical bar
/// <c>BookEventHandlerTests</c> already holds <c>BookEventHandler</c> to. What the database and Redis
/// do under contention is the integration/concurrency suites' job.
/// </summary>
public class PhoneVerificationHandlerTests
{
    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    public class Initiate
    {
        [Fact]
        public async Task ASuccessfulInitiate_PersistsTheRowAndSendsTheCode()
        {
            var world = new InitiateWorld();

            var result = await world.HandleAsync();

            Assert.True(result.IsSuccess);

            var saved = Assert.Single(world.PendingVerifications.Saved);
            Assert.Equal(BookingFixtures.TenantId, saved.TenantId);
            Assert.Equal(BookingFixtures.Phone, saved.Phone);
            Assert.Equal(Hash(InitiateWorld.Code), saved.CodeHash);

            var sent = Assert.Single(world.Sender.Sent);
            Assert.Equal(BookingFixtures.Phone, sent.Phone);
            Assert.Equal(InitiateWorld.Code, sent.Code);
            Assert.Equal(PhoneVerificationDeliveryMethod.Sms, sent.Method);

            Assert.Equal(saved.Id.Value, result.Value.PendingPhoneVerificationId);
            Assert.Equal(saved.ExpiresAt, result.Value.ExpiresAt);
        }

        [Fact]
        public async Task AnInvalidPhone_IsRejectedBeforeAnyCalendarLookup()
        {
            var world = new InitiateWorld();

            var result = await world.HandleAsync(phone: "not-a-phone");

            Assert.True(result.IsFailure);
            Assert.Equal("phone_verification.invalid_phone", result.Error!.Value.Code);
            Assert.Empty(world.Limiter.Checked);
            Assert.Empty(world.PendingVerifications.Saved);
            Assert.Empty(world.Sender.Sent);
        }

        [Fact]
        public async Task AnUnknownCalendar_IsRejected()
        {
            var world = new InitiateWorld(calendarExists: false);

            var result = await world.HandleAsync();

            Assert.True(result.IsFailure);
            Assert.Equal("phone_verification.calendar_not_found", result.Error!.Value.Code);
        }

        [Fact]
        public async Task AnotherTenantsApprovedOrigin_IsRejectedBeforeAnyRateLimitIsSpent()
        {
            var world = new InitiateWorld();

            var result = await world.HandleAsync(origin: "https://other.example");

            Assert.True(result.IsFailure);
            Assert.Equal("phone_verification.calendar_not_found", result.Error!.Value.Code);
            Assert.Empty(world.Limiter.Checked);
        }

        [Fact]
        public async Task APhoneOverItsOwnBucket_IsRateLimitedBeforeTheIpOrCalendarBucketIsChecked()
        {
            var world = new InitiateWorld();
            world.Limiter.Deny("phone-verification:phone:");

            var result = await world.HandleAsync();

            Assert.True(result.IsFailure);
            Assert.Equal("phone_verification.rate_limited", result.Error!.Value.Code);
            Assert.Single(world.Limiter.Checked);
            Assert.Empty(world.PendingVerifications.Saved);
            Assert.Empty(world.Sender.Sent);
        }

        [Fact]
        public async Task AnIpOverItsOwnBucket_IsRateLimitedBeforeTheCalendarBucketIsChecked()
        {
            var world = new InitiateWorld();
            world.Limiter.Deny("phone-verification:ip:");

            var result = await world.HandleAsync();

            Assert.True(result.IsFailure);
            Assert.Equal("phone_verification.rate_limited", result.Error!.Value.Code);
            Assert.Equal(2, world.Limiter.Checked.Count);
        }

        [Fact]
        public async Task WithNoCallerIp_TheIpBucketIsNeverChecked()
        {
            var world = new InitiateWorld();

            var result = await world.HandleAsync(callerIp: null);

            Assert.True(result.IsSuccess);
            Assert.DoesNotContain(world.Limiter.Checked, key => key.StartsWith("phone-verification:ip:", StringComparison.Ordinal));
        }

        [Fact]
        public async Task ACalendarOverItsOwnBucket_IsRateLimited()
        {
            var world = new InitiateWorld();
            world.Limiter.Deny("phone-verification:calendar:");

            var result = await world.HandleAsync();

            Assert.True(result.IsFailure);
            Assert.Equal("phone_verification.rate_limited", result.Error!.Value.Code);
            Assert.Equal(3, world.Limiter.Checked.Count);
        }

        private sealed class InitiateWorld
        {
            public const string Code = "482913";

            private readonly InitiatePhoneVerificationHandler _handler;

            public InitiateWorld(bool calendarExists = true)
            {
                var calendar = BookingFixtures.Calendar();
                _handler = new InitiatePhoneVerificationHandler(
                    new FakeCalendarRepository(calendarExists ? calendar : null),
                    new FakeTenantRepository(BookingFixtures.Tenant()),
                    PendingVerifications,
                    new FixedPhoneVerificationCodeGenerator(Code),
                    Sender,
                    Limiter,
                    new PhoneVerificationOptions(),
                    new PhoneVerificationRateLimitOptions(),
                    new SequentialIdGenerator(),
                    new FakeClock(BookingFixtures.Now));
            }

            public FakePendingPhoneVerificationRepository PendingVerifications { get; } = new();

            public FakePhoneVerificationSender Sender { get; } = new();

            public FakeRateLimiter Limiter { get; } = new();

            public Task<Result<InitiatedPhoneVerification>> HandleAsync(
                string? phone = null, string? origin = null, string? callerIp = "203.0.113.7") =>
                _handler.HandleAsync(
                    new InitiatePhoneVerification(
                        BookingFixtures.CalendarId, phone ?? BookingFixtures.Phone, origin, callerIp),
                    CancellationToken.None);
        }
    }

    public class Confirm
    {
        [Fact]
        public async Task ACorrectCode_ReturnsAFreshProofToken()
        {
            var world = new ConfirmWorld();

            var result = await world.HandleAsync();

            Assert.True(result.IsSuccess);
            Assert.Equal(ConfirmWorld.ProofToken, result.Value.ProofToken);
            Assert.Equal(world.Verification.Id.Value, result.Value.PendingPhoneVerificationId);
            Assert.Equal(BookingFixtures.Now + world.Options.ProofValidFor, result.Value.ProofExpiresAt);
        }

        [Fact]
        public async Task ACorrectCode_PersistsTheProofHashNotThePlaintext()
        {
            var world = new ConfirmWorld();

            await world.HandleAsync();

            var saved = Assert.Single(world.PendingVerifications.Saved);
            Assert.Equal(Hash(ConfirmWorld.ProofToken), saved.ProofTokenHash);
        }

        [Fact]
        public async Task AWrongCode_IsRefusedAndTheRowIsSavedWithTheIncrementedAttempt()
        {
            var world = new ConfirmWorld();

            var result = await world.HandleAsync(code: "000000");

            Assert.True(result.IsFailure);
            Assert.Equal("phone_verification.wrong_code", result.Error!.Value.Code);
            Assert.Equal(1, Assert.Single(world.PendingVerifications.Saved).AttemptCount);
        }

        [Fact]
        public async Task AnExpiredCode_IsRefused()
        {
            var world = new ConfirmWorld(clockAt: BookingFixtures.Now.AddMinutes(11));

            var result = await world.HandleAsync();

            Assert.True(result.IsFailure);
            Assert.Equal("phone_verification.expired", result.Error!.Value.Code);
        }

        [Fact]
        public async Task ALockedOutRow_IsRefused()
        {
            var world = new ConfirmWorld(maxAttempts: 1);
            await world.HandleAsync(code: "000000");

            var result = await world.HandleAsync();

            Assert.True(result.IsFailure);
            Assert.Equal("phone_verification.locked_out", result.Error!.Value.Code);
        }

        [Fact]
        public async Task AnAlreadyConsumedRow_IsRefused()
        {
            var world = new ConfirmWorld();
            await world.HandleAsync();

            var result = await world.HandleAsync();

            Assert.True(result.IsFailure);
            Assert.Equal("phone_verification.already_consumed", result.Error!.Value.Code);
        }

        [Fact]
        public async Task AnUnknownVerificationId_IsRefused()
        {
            var world = new ConfirmWorld();

            var result = await world.HandleAsync(id: Guid.NewGuid());

            Assert.True(result.IsFailure);
            Assert.Equal("phone_verification.not_found", result.Error!.Value.Code);
        }

        /// <summary>Cross-tenant collapse: a real row id that belongs to a different tenant's calendar
        /// reads exactly like an unknown one.</summary>
        [Fact]
        public async Task ARowBelongingToAnotherTenant_IsRefusedAsNotFound()
        {
            var otherTenantVerification = PendingPhoneVerification.Request(
                new PendingPhoneVerificationId(Guid.NewGuid()), BookingFixtures.OtherTenantId,
                new PhoneNumber(BookingFixtures.Phone), Hash("111111"), PhoneVerificationDeliveryMethod.Sms,
                BookingFixtures.Now, TimeSpan.FromMinutes(10), 5);
            var world = new ConfirmWorld(seed: otherTenantVerification);

            var result = await world.HandleAsync(id: otherTenantVerification.Id.Value);

            Assert.True(result.IsFailure);
            Assert.Equal("phone_verification.not_found", result.Error!.Value.Code);
        }

        private sealed class ConfirmWorld
        {
            public const string Code = "482913";
            public const string ProofToken = "proof-token-abc";

            private readonly ConfirmPhoneVerificationHandler _handler;

            public ConfirmWorld(
                DateTimeOffset? clockAt = null, int maxAttempts = 5, PendingPhoneVerification? seed = null)
            {
                Verification = seed ?? PendingPhoneVerification.Request(
                    new PendingPhoneVerificationId(Guid.NewGuid()), BookingFixtures.TenantId,
                    new PhoneNumber(BookingFixtures.Phone), Hash(Code), PhoneVerificationDeliveryMethod.Sms,
                    BookingFixtures.Now, TimeSpan.FromMinutes(10), maxAttempts);
                PendingVerifications = new FakePendingPhoneVerificationRepository(Verification);

                _handler = new ConfirmPhoneVerificationHandler(
                    new FakeCalendarRepository(BookingFixtures.Calendar()),
                    new FakeTenantRepository(BookingFixtures.Tenant()),
                    PendingVerifications,
                    new FixedPhoneVerificationProofTokenGenerator(ProofToken),
                    Options,
                    new FakeClock(clockAt ?? BookingFixtures.Now));
            }

            public PendingPhoneVerification Verification { get; }

            public FakePendingPhoneVerificationRepository PendingVerifications { get; }

            public PhoneVerificationOptions Options { get; } = new();

            public Task<Result<ConfirmedPhoneVerification>> HandleAsync(string? code = null, Guid? id = null) =>
                _handler.HandleAsync(
                    new ConfirmPhoneVerification(
                        BookingFixtures.CalendarId, id ?? Verification.Id.Value, code ?? Code, Origin: null),
                    CancellationToken.None);
        }
    }
}
