using System.Security.Cryptography;
using System.Text;
using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Application.UseCases.BookEvent;
using Ago.Calendar.Application.UseCases.PublicBooking;
using Ago.Calendar.Domain;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.PhoneVerification;

/// <summary>
/// `20-10`: the confirm half - structurally mirroring `ago-chat`'s own
/// <c>ConfirmPhoneVerificationHandler</c> (`14-15`) without referencing that assembly, and diverging
/// from it in exactly one place: what a <see cref="PhoneVerificationConfirmOutcome.Confirmed"/> outcome
/// produces. `14-15`'s own confirm step links a <c>ChannelIdentity</c> a chat visitor's own session
/// already knows how to present again. This item's caller is an anonymous browser with no session at
/// all, so this handler mints a fresh bearer proof instead
/// (<see cref="PendingPhoneVerification.IssueProof"/>) - see that type's own remarks for the full
/// reasoning.
/// </summary>
public sealed class ConfirmPhoneVerificationHandler(
    IBookingCalendarRepository calendars,
    ITenantRepository tenants,
    IPendingPhoneVerificationRepository pendingVerifications,
    IPhoneVerificationProofTokenGenerator proofTokenGenerator,
    PhoneVerificationOptions options,
    IClock clock)
{
    public async Task<Result<ConfirmedPhoneVerification>> HandleAsync(
        ConfirmPhoneVerification command, CancellationToken cancellationToken)
    {
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

        var verification = await pendingVerifications.GetByIdAsync(
            new PendingPhoneVerificationId(command.PendingPhoneVerificationId), cancellationToken);
        if (verification is null || verification.TenantId != calendar.TenantId)
        {
            // Wrong-tenant or unknown reads like "not found" - the same cross-tenant info-hiding shape
            // every other lookup on this unauthenticated surface already uses.
            return PhoneVerificationErrors.NotFound();
        }

        var now = clock.UtcNow;
        var submittedHash = SHA256.HashData(Encoding.UTF8.GetBytes(command.Code ?? string.Empty));
        var outcome = verification.AttemptConfirm(submittedHash, now);

        switch (outcome)
        {
            case PhoneVerificationConfirmOutcome.AlreadyConsumed:
                // No mutation happened (PendingPhoneVerification.AttemptConfirm's own remarks) -
                // nothing to save.
                return PhoneVerificationErrors.AlreadyConsumed();

            case PhoneVerificationConfirmOutcome.Expired:
                return PhoneVerificationErrors.Expired();

            case PhoneVerificationConfirmOutcome.LockedOut:
                // May or may not have just incremented AttemptCount on this very call - saved
                // unconditionally either way, the same "a harmless no-op update either way" call
                // `ago-chat`'s own handler makes.
                await pendingVerifications.SaveAsync(verification, cancellationToken);
                return PhoneVerificationErrors.LockedOut();

            case PhoneVerificationConfirmOutcome.WrongCode:
                await pendingVerifications.SaveAsync(verification, cancellationToken);
                return PhoneVerificationErrors.WrongCode();

            case PhoneVerificationConfirmOutcome.Confirmed:
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unhandled phone verification confirm outcome.");
        }

        var proofToken = proofTokenGenerator.NewToken();
        var proofTokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(proofToken));
        var proofExpiresAt = now + options.ProofValidFor;
        verification.IssueProof(proofTokenHash, proofExpiresAt);

        // One SaveChangesAsync for both the consumption and the proof issuance - they are the same
        // aggregate, in the same request, with nothing else that could observe the row in between; not
        // two separate saves the way ago-chat's own handler crash-orders its consumption ahead of a
        // *second* aggregate (ChannelIdentity). There is no second aggregate here.
        await pendingVerifications.SaveAsync(verification, cancellationToken);

        return new ConfirmedPhoneVerification(verification.Id.Value, proofToken, proofExpiresAt);
    }
}
