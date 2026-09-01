using System.Security.Cryptography;
using System.Text;
using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.UseCases.BookEvent;

/// <summary>
/// `20-10`: resolves whether <c>BookEvent.Phone</c> counts as verified for this claim, trying three
/// sources in order. Not named <c>*Handler</c> and not a use case, the identical naming call
/// <see cref="Ago.Calendar.Application.UseCases.PublicBooking.EmbedScopeResolver"/> already makes for
/// itself: this orchestrates nothing a caller asked for, it resolves a fact
/// <see cref="BookEventHandler"/>'s own use case needs.
///
/// <list type="number">
///   <item><b>An assertion already supplied directly on the command</b> - the chat-originated flow's
///   own `20-09` shape, unchanged: <see cref="Ago.Calendar.Application.UseCases.BookEvent.BookEvent.PhoneVerifiedAt"/>
///   is trusted as-is when present, so this resolver is a pure no-op, no extra query, on the one path
///   that has always worked.</item>
///   <item><b>A returning customer's own <c>Customer.PhoneVerifiedAt</c></b> - `20-10`'s own Scope item
///   (b): a phone already verified from an earlier booking (chat-originated or, after this item, public)
///   needs no fresh round.</item>
///   <item><b>A freshly confirmed <see cref="PendingPhoneVerification"/>, presented as a proof
///   token</b> - `20-10`'s own new mechanism, checked last because it is the only one of the three that
///   costs a second query and a cryptographic comparison.</item>
/// </list>
///
/// <para><b>Why this lives beside <see cref="BookEventHandler"/> rather than inside it.</b> Three
/// sources tried in order, each with its own early-out, read like a distinct concern from "load the
/// calendar, check rates, claim the slot" - and unlike <c>EmbedScopeResolver</c>'s own three callers,
/// this type has exactly one, but the same reasoning for pulling it out still holds: a reviewer scanning
/// <see cref="BookEventHandler.HandleAsync"/> for "what does a claim actually require" should not have
/// to read a customer lookup and a token hash comparison inline to find out.</para>
/// </summary>
public sealed class PhoneVerificationAssertionResolver(
    ICustomerRepository customers, IPendingPhoneVerificationRepository pendingVerifications)
{
    public async Task<DateTimeOffset?> ResolveAsync(
        TenantId tenantId,
        PhoneNumber phone,
        DateTimeOffset? assertedAt,
        Guid? pendingPhoneVerificationId,
        string? proofToken,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (assertedAt is not null)
        {
            return assertedAt;
        }

        var existingCustomer = await customers.FindByPhoneAsync(tenantId, phone, cancellationToken);
        if (existingCustomer?.PhoneVerifiedAt is { } previouslyVerifiedAt)
        {
            return previouslyVerifiedAt;
        }

        if (pendingPhoneVerificationId is not { } id || string.IsNullOrEmpty(proofToken))
        {
            return null;
        }

        var verification = await pendingVerifications.GetByIdAsync(
            new PendingPhoneVerificationId(id), cancellationToken);
        if (verification is null || verification.TenantId != tenantId)
        {
            return null;
        }

        var submittedProofHash = SHA256.HashData(Encoding.UTF8.GetBytes(proofToken));
        return verification.IsProofValid(phone.Value, submittedProofHash, now)
            ? verification.ConsumedAt
            : null;
    }
}
