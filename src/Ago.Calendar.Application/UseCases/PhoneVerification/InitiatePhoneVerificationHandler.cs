using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Application.UseCases.BookEvent;
using Ago.Calendar.Application.UseCases.PublicBooking;
using Ago.Calendar.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.PhoneVerification;

/// <summary>
/// `20-10`: the send-triggering half of Calendar's own phone verification, structurally mirroring
/// `ago-chat`'s own <c>InitiatePhoneVerificationHandler</c> (`14-15`) without referencing that assembly.
///
/// <para><b>Resolves the calendar/tenant and checks the origin the identical way
/// <see cref="Ago.Calendar.Application.UseCases.BookEvent.BookEventHandler"/> does</b>, and collapses
/// every failure into <see cref="PhoneVerificationErrors.CalendarNotFound"/> for the identical
/// info-hiding reason: this endpoint is unauthenticated and reachable by anyone.</para>
///
/// <para><b>Calls <see cref="IPhoneVerificationSender"/> directly, inline - not via an outbox and a
/// `Ago.Calendar.Worker` consumer the way `14-15` dispatches its own send.</b> See
/// <see cref="IPhoneVerificationSender"/>'s own remarks for the full reasoning: the risk that
/// justifies `14-15`'s own indirection (a paid gateway call that can hang) does not apply to this
/// item's only shipped implementation, <c>FakePhoneVerificationSender</c>, and AGO Calendar has no
/// outbox-dispatch pipeline for anything to reuse today.</para>
/// </summary>
public sealed class InitiatePhoneVerificationHandler(
    IBookingCalendarRepository calendars,
    ITenantRepository tenants,
    IPendingPhoneVerificationRepository pendingVerifications,
    IPhoneVerificationCodeGenerator codeGenerator,
    IPhoneVerificationSender sender,
    IRateLimiter rateLimiter,
    PhoneVerificationOptions options,
    PhoneVerificationRateLimitOptions rateLimitOptions,
    IIdGenerator idGenerator,
    IClock clock)
{
    public async Task<Result<InitiatedPhoneVerification>> HandleAsync(
        InitiatePhoneVerification command, CancellationToken cancellationToken)
    {
        PhoneNumber phone;
        try
        {
            phone = new PhoneNumber(command.Phone ?? string.Empty);
        }
        catch (ArgumentException exception)
        {
            return PhoneVerificationErrors.InvalidPhone(exception.Message);
        }

        var calendar = await calendars.GetByIdAsync(command.CalendarId, cancellationToken);
        if (calendar is null || !calendar.IsPublished)
        {
            return PhoneVerificationErrors.CalendarNotFound();
        }

        var tenant = await tenants.GetByIdAsync(calendar.TenantId, cancellationToken);
        if (tenant is null || !OriginPolicy.IsAcceptable(tenant, command.Origin))
        {
            return PhoneVerificationErrors.CalendarNotFound();
        }

        // Phone bucket first, then IP, then calendar - the ordering PhoneVerificationRateLimitOptions's
        // own remarks reason through.
        var phoneLimit = await rateLimiter.CheckAsync(
            PhoneBucket(calendar.TenantId, phone),
            new RateLimitRule(rateLimitOptions.PerPhoneCapacity, rateLimitOptions.PerPhoneRefillPerSecond),
            cancellationToken);
        if (!phoneLimit.Allowed)
        {
            return PhoneVerificationErrors.RateLimited(phoneLimit.RetryAfter);
        }

        if (!string.IsNullOrWhiteSpace(command.CallerIp))
        {
            var ipLimit = await rateLimiter.CheckAsync(
                IpBucket(calendar.TenantId, command.CallerIp),
                new RateLimitRule(rateLimitOptions.PerIpCapacity, rateLimitOptions.PerIpRefillPerSecond),
                cancellationToken);
            if (!ipLimit.Allowed)
            {
                return PhoneVerificationErrors.RateLimited(ipLimit.RetryAfter);
            }
        }

        var calendarLimit = await rateLimiter.CheckAsync(
            new RateLimitKey($"phone-verification:calendar:{calendar.Id.Value}"),
            new RateLimitRule(rateLimitOptions.PerCalendarCapacity, rateLimitOptions.PerCalendarRefillPerSecond),
            cancellationToken);
        if (!calendarLimit.Allowed)
        {
            return PhoneVerificationErrors.RateLimited(calendarLimit.RetryAfter);
        }

        var now = clock.UtcNow;
        var code = codeGenerator.NewCode();
        var codeHash = SHA256.HashData(Encoding.UTF8.GetBytes(code));

        var verification = PendingPhoneVerification.Request(
            new PendingPhoneVerificationId(idGenerator.NewId(now)), calendar.TenantId, phone, codeHash,
            options.DefaultDeliveryMethod, now, options.ValidFor, options.MaxAttempts);

        await pendingVerifications.SaveAsync(verification, cancellationToken);

        // See this type's own remarks for why this is a direct, inline call rather than an
        // outbox-plus-consumer relay.
        await sender.SendCodeAsync(
            new PhoneVerificationDelivery(phone.Value, code, verification.DeliveryMethod), cancellationToken);

        return new InitiatedPhoneVerification(
            verification.Id.Value, verification.ExpiresAt, verification.DeliveryMethod.ToString());
    }

    /// <summary>Hashed for the identical personal-data reason <c>BookEventHandler.PhoneBucket</c>'s own
    /// remarks give: a rate-limit key is visible to anybody who can read Redis, and a phone number is
    /// this product's most directly identifying field.</summary>
    private static RateLimitKey PhoneBucket(TenantId tenantId, PhoneNumber phone)
    {
        var material = string.Create(CultureInfo.InvariantCulture, $"{tenantId.Value:N}:{phone.Value}");
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return new RateLimitKey($"phone-verification:phone:{Convert.ToHexStringLower(digest)}");
    }

    /// <summary>Hashed for the same reason the phone bucket is: an IP address is personal data too, and
    /// nothing about a rate-limit key needs it to be readable by eye.</summary>
    private static RateLimitKey IpBucket(TenantId tenantId, string callerIp)
    {
        var material = string.Create(CultureInfo.InvariantCulture, $"{tenantId.Value:N}:{callerIp}");
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return new RateLimitKey($"phone-verification:ip:{Convert.ToHexStringLower(digest)}");
    }
}
