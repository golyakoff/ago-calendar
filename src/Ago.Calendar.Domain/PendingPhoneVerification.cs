using System.Security.Cryptography;

namespace Ago.Calendar.Domain;

/// <summary>
/// `20-10`: AGO Calendar's own phone-verification primitive for the public, unauthenticated booking
/// widget - a second, independent aggregate from <c>ago-chat</c>'s own <c>PendingPhoneVerification</c>
/// (`14-15`), mirroring its shape (code hash, expiry, attempt count/lockout, constant-time compare)
/// without referencing that assembly at all: `adr/0027` requires AGO Calendar to be independently
/// deployable, and the two products do not even share a database.
///
/// <para><b>No <c>VisitorId</c>, no conversation - the one real structural difference from the
/// `ago-chat` original.</b> That type's own owner is a chat visitor with a session; this endpoint is
/// reached anonymously, by a stranger's browser, with nothing to attach a verification to except the
/// tenant and the phone number itself. <see cref="TenantId"/> replaces <c>SiteId</c> for the identical
/// tenant-scoping reason, and there is no visitor field at all.</para>
///
/// <para><b><see cref="ProofTokenHash"/>/<see cref="ProofExpiresAt"/> - the one real addition this
/// aggregate makes over the `ago-chat` original, and why.</b> `14-15`'s own confirm step hands its
/// caller a <c>ChannelIdentityId</c> - a permanent row the caller's own session already knows how to
/// present again, because a chat visitor is never anonymous to the conversation it is inside. The
/// public booking widget has no session at all: the confirm step and the eventual
/// <c>POST .../book</c> call are two unrelated HTTP requests from an anonymous browser, so this
/// aggregate has to hand back something the caller can *present* as proof next time - a bearer secret,
/// generated fresh, hashed and stored the identical way the code itself is (never logged, never
/// returned twice). <see cref="IssueProof"/> mints it, once, only on a confirmed row; <see cref="IsProofValid"/>
/// is the one place that secret is ever checked.</para>
///
/// <para><b>Lockout, not just expiry.</b> The identical reasoning `14-15`'s own aggregate gives for
/// itself: this type's confirmation is an interactive "submit a guess" endpoint a script could hammer,
/// unlike a passive inbound-message match, so <see cref="AttemptCount"/>/<see cref="MaxAttempts"/> is
/// what stops that.</para>
///
/// <para><b><see cref="CodeHash"/>/<see cref="ProofTokenHash"/>, hashed, with the identical caveat the
/// code hash carries in `14-15`</b>: the six-digit code is deliberately low entropy (a human reads it
/// off an SMS and types it back), so its own hash buys no brute-force resistance a determined attacker
/// with the row in hand could not already get - <see cref="MaxAttempts"/> plus the short
/// <see cref="ExpiresAt"/> window is the real security. The proof token is the opposite case - high
/// entropy, never typed by a human - so its hash genuinely does resist an attacker who reads the row;
/// both are hashed anyway for one uniform "never store a bearer-shaped value in plaintext"
/// discipline.</para>
///
/// <para><b>Returns outcomes, never throws, on <see cref="AttemptConfirm"/> - the identical reasoning
/// `14-15`'s own type gives.</b> A wrong code, an expired code and a lockout are each an ordinary,
/// expected outcome a real visitor routinely hits, not a caller bug worth an exception.</para>
/// </summary>
public sealed class PendingPhoneVerification
{
    public PendingPhoneVerificationId Id { get; }

    public TenantId TenantId { get; }

    /// <summary>Canonical E.164 form (<see cref="PhoneNumber"/>) - the one normalised string reused
    /// unchanged for the rate-limit key, this column, and the eventual <c>BookingAttempt.PhoneVerifiedAt</c>
    /// comparison, exactly as `20-09`'s own <c>Customer.Phone</c> already establishes for this
    /// product.</summary>
    public string Phone { get; } = string.Empty;

    public byte[] CodeHash { get; } = [];

    public PhoneVerificationDeliveryMethod DeliveryMethod { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset ExpiresAt { get; }

    public DateTimeOffset? ConsumedAt { get; private set; }

    /// <summary>Wrong-code guesses only - a correct confirmation never increments this, and an
    /// expiry/lockout check that refuses before ever comparing the code does not either
    /// (<see cref="AttemptConfirm"/>'s own remarks on the check order).</summary>
    public int AttemptCount { get; private set; }

    public int MaxAttempts { get; }

    /// <summary>Set once, by <see cref="IssueProof"/>, only on a row that has already reached
    /// <see cref="PhoneVerificationConfirmOutcome.Confirmed"/>. Never present on a row nobody has
    /// confirmed yet - <see cref="IsProofValid"/> refuses when this is null rather than treating an
    /// absent proof as a wildcard.</summary>
    public byte[]? ProofTokenHash { get; private set; }

    /// <summary>The proof's own, separate expiry - deliberately not the same field as
    /// <see cref="ExpiresAt"/> (the *code's* window, which is already spent the moment
    /// <see cref="ConsumedAt"/> is set). A confirmed visitor who does not complete the booking
    /// immediately still has a bounded window to do so, not an indefinitely replayable secret.</summary>
    public DateTimeOffset? ProofExpiresAt { get; private set; }

    public bool IsLockedOut => AttemptCount >= MaxAttempts;

    private PendingPhoneVerification(
        PendingPhoneVerificationId id, TenantId tenantId, string phone, byte[] codeHash,
        PhoneVerificationDeliveryMethod deliveryMethod, DateTimeOffset createdAt, DateTimeOffset expiresAt,
        int maxAttempts, DateTimeOffset? consumedAt, int attemptCount, byte[]? proofTokenHash,
        DateTimeOffset? proofExpiresAt)
    {
        Id = id;
        TenantId = tenantId;
        Phone = phone;
        CodeHash = codeHash;
        DeliveryMethod = deliveryMethod;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
        MaxAttempts = maxAttempts;
        ConsumedAt = consumedAt;
        AttemptCount = attemptCount;
        ProofTokenHash = proofTokenHash;
        ProofExpiresAt = proofExpiresAt;
    }

    // EF Core materialization only (1-04's precedent, restated here for this product) - never called
    // by domain code.
    private PendingPhoneVerification()
    {
    }

    /// <summary>Issues a fresh pending verification. The caller
    /// (<c>InitiatePhoneVerificationHandler</c>) has already generated <paramref name="code"/> and
    /// hashed it into <paramref name="codeHash"/> - this factory receives the hash for
    /// <see cref="CodeHash"/> and never sees the plaintext at all, unlike `14-15`'s own factory, which
    /// carries it one hop further onto a domain event bound for an outbox-driven worker send. This
    /// item's own <c>InitiatePhoneVerificationHandler</c> calls <c>IPhoneVerificationSender</c>
    /// directly instead (see that handler's own remarks for why), so there is no event for the
    /// plaintext to ride on and no reason for this factory to see it.</summary>
    public static PendingPhoneVerification Request(
        PendingPhoneVerificationId id, TenantId tenantId, PhoneNumber phone, byte[] codeHash,
        PhoneVerificationDeliveryMethod deliveryMethod, DateTimeOffset now, TimeSpan validFor, int maxAttempts) =>
        new(
            id, tenantId, phone.Value, codeHash, deliveryMethod, now, now + validFor, maxAttempts,
            consumedAt: null, attemptCount: 0, proofTokenHash: null, proofExpiresAt: null);

    /// <summary>
    /// The one write path a wrong guess, an expired window, or a lockout all pass through. Checked in
    /// the order that lets a caller who was never going to succeed find out as cheaply as possible:
    /// already consumed (a genuine race - two confirmations for the same row) first, then the window,
    /// then the lockout, and only then - the one comparison that costs anything - the code itself.
    ///
    /// <para>Constant-time comparison (<see cref="CryptographicOperations.FixedTimeEquals"/>): a
    /// variable-time compare would leak how many leading bytes matched through response timing, a
    /// real (if narrow, given the short code space) side channel.</para>
    /// </summary>
    public PhoneVerificationConfirmOutcome AttemptConfirm(byte[] submittedCodeHash, DateTimeOffset now)
    {
        if (ConsumedAt is not null)
        {
            return PhoneVerificationConfirmOutcome.AlreadyConsumed;
        }

        if (now >= ExpiresAt)
        {
            return PhoneVerificationConfirmOutcome.Expired;
        }

        if (IsLockedOut)
        {
            return PhoneVerificationConfirmOutcome.LockedOut;
        }

        if (!CryptographicOperations.FixedTimeEquals(submittedCodeHash, CodeHash))
        {
            AttemptCount++;
            return IsLockedOut ? PhoneVerificationConfirmOutcome.LockedOut : PhoneVerificationConfirmOutcome.WrongCode;
        }

        ConsumedAt = now;
        return PhoneVerificationConfirmOutcome.Confirmed;
    }

    /// <summary>Mints the bearer proof a confirmed visitor presents to <c>POST .../book</c> - see this
    /// type's own remarks for why the public widget needs one at all, unlike `14-15`'s own aggregate.
    /// Only callable once <see cref="AttemptConfirm"/> has actually returned
    /// <see cref="PhoneVerificationConfirmOutcome.Confirmed"/>: the caller
    /// (<c>ConfirmPhoneVerificationHandler</c>) never reaches this on any other outcome, so a null
    /// <see cref="ConsumedAt"/> here is a caller bug, not an expected state - which is why this throws
    /// rather than returning an outcome, unlike <see cref="AttemptConfirm"/> itself.</summary>
    public void IssueProof(byte[] proofTokenHash, DateTimeOffset expiresAt)
    {
        if (ConsumedAt is null)
        {
            throw new InvalidOperationException(
                "A proof can only be issued for a phone verification that has actually been confirmed.");
        }

        ProofTokenHash = proofTokenHash;
        ProofExpiresAt = expiresAt;
    }

    /// <summary>
    /// The book-time check: is this a live proof, presented by someone who actually holds the token,
    /// for the exact phone number this row was confirmed for? The critical property `20-10`'s own
    /// backlog file names - a caller must not be able to verify phone A and then book with phone B
    /// using that same token - is the <paramref name="phone"/> comparison below; everything else here
    /// is the ordinary bearer-credential checks (possession, liveness) `AttemptConfirm`'s own code
    /// check already establishes the discipline for.
    ///
    /// <para>A business rule, not an orchestration step, which is why it lives here rather than as an
    /// <c>if</c> inside <c>BookEventHandler</c> - clean-architecture.md: "an <c>if</c> about business
    /// meaning inside a handler is misplaced."</para>
    /// </summary>
    public bool IsProofValid(string phone, byte[] submittedProofHash, DateTimeOffset now)
    {
        if (ConsumedAt is null || ProofTokenHash is null || ProofExpiresAt is null)
        {
            return false;
        }

        if (now >= ProofExpiresAt)
        {
            return false;
        }

        if (!string.Equals(Phone, phone, StringComparison.Ordinal))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(submittedProofHash, ProofTokenHash);
    }
}
