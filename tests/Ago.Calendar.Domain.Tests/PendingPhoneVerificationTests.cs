using System.Security.Cryptography;
using System.Text;

namespace Ago.Calendar.Domain.Tests;

/// <summary>
/// `20-10`'s own second Done-when box: "a wrong code is refused, a code is locked out after too many
/// wrong attempts, and an expired code is refused - the same guarantees `14-15`'s own domain logic
/// already proves, proven again here since this is a second aggregate, not a shared one." Mirrors
/// `ago-chat`'s own <c>PendingPhoneVerificationTests</c> test-by-test where the aggregate shape is the
/// same, and adds <see cref="PendingPhoneVerification.IssueProof"/>/<see cref="PendingPhoneVerification.IsProofValid"/>
/// coverage for this item's own addition over the `ago-chat` original.
/// </summary>
public class PendingPhoneVerificationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TenantId TenantId = new(Guid.NewGuid());
    private const string Code = "482913";
    private const int DefaultMaxAttempts = 5;
    private const string Phone = "+79991234567";

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    private static PendingPhoneVerification Request(
        TimeSpan? validFor = null, int maxAttempts = DefaultMaxAttempts, string code = Code, string phone = Phone) =>
        PendingPhoneVerification.Request(
            new PendingPhoneVerificationId(Guid.NewGuid()), TenantId, new PhoneNumber(phone), Hash(code),
            PhoneVerificationDeliveryMethod.Sms, Now, validFor ?? TimeSpan.FromMinutes(10), maxAttempts);

    [Fact]
    public void Request_StartsUnconsumed()
    {
        var verification = Request();

        Assert.Null(verification.ConsumedAt);
    }

    [Fact]
    public void Request_SetsExpiresAtToNowPlusValidFor()
    {
        var verification = Request(TimeSpan.FromMinutes(10));

        Assert.Equal(Now + TimeSpan.FromMinutes(10), verification.ExpiresAt);
    }

    [Fact]
    public void Request_StartsWithZeroAttempts()
    {
        var verification = Request();

        Assert.Equal(0, verification.AttemptCount);
    }

    [Fact]
    public void Request_StoresCanonicalPhoneValue()
    {
        var verification = PendingPhoneVerification.Request(
            new PendingPhoneVerificationId(Guid.NewGuid()), TenantId, new PhoneNumber("+7 (999) 123-45-67"),
            Hash(Code), PhoneVerificationDeliveryMethod.Sms, Now, TimeSpan.FromMinutes(10), DefaultMaxAttempts);

        Assert.Equal("+79991234567", verification.Phone);
    }

    [Fact]
    public void Request_StartsWithNoProof()
    {
        var verification = Request();

        Assert.Null(verification.ProofTokenHash);
        Assert.Null(verification.ProofExpiresAt);
    }

    [Fact]
    public void AttemptConfirm_WithCorrectCode_ReturnsConfirmed()
    {
        var verification = Request(code: Code);

        var outcome = verification.AttemptConfirm(Hash(Code), Now.AddMinutes(1));

        Assert.Equal(PhoneVerificationConfirmOutcome.Confirmed, outcome);
    }

    [Fact]
    public void AttemptConfirm_WithCorrectCode_SetsConsumedAt()
    {
        var verification = Request(code: Code);
        var confirmedAt = Now.AddMinutes(1);

        verification.AttemptConfirm(Hash(Code), confirmedAt);

        Assert.Equal(confirmedAt, verification.ConsumedAt);
    }

    [Fact]
    public void AttemptConfirm_WithWrongCode_ReturnsWrongCode()
    {
        var verification = Request(code: Code);

        var outcome = verification.AttemptConfirm(Hash("000000"), Now.AddMinutes(1));

        Assert.Equal(PhoneVerificationConfirmOutcome.WrongCode, outcome);
    }

    [Fact]
    public void AttemptConfirm_WithWrongCode_IncrementsAttemptCount()
    {
        var verification = Request(code: Code);

        verification.AttemptConfirm(Hash("000000"), Now.AddMinutes(1));

        Assert.Equal(1, verification.AttemptCount);
    }

    [Fact]
    public void AttemptConfirm_WhenAlreadyConsumed_ReturnsAlreadyConsumed()
    {
        var verification = Request(code: Code);
        verification.AttemptConfirm(Hash(Code), Now.AddMinutes(1));

        var outcome = verification.AttemptConfirm(Hash(Code), Now.AddMinutes(2));

        Assert.Equal(PhoneVerificationConfirmOutcome.AlreadyConsumed, outcome);
    }

    [Fact]
    public void AttemptConfirm_WhenExpired_ReturnsExpired()
    {
        var verification = Request(TimeSpan.FromMinutes(10), code: Code);

        var outcome = verification.AttemptConfirm(Hash(Code), verification.ExpiresAt);

        Assert.Equal(PhoneVerificationConfirmOutcome.Expired, outcome);
    }

    /// <summary>An expired row never spends an attempt on a wrong guess either - checked before the
    /// code comparison (<see cref="PendingPhoneVerification.AttemptConfirm"/>'s own remarks on check
    /// ordering).</summary>
    [Fact]
    public void AttemptConfirm_WhenExpiredWithWrongCode_DoesNotIncrementAttemptCount()
    {
        var verification = Request(TimeSpan.FromMinutes(10));

        verification.AttemptConfirm(Hash("000000"), verification.ExpiresAt);

        Assert.Equal(0, verification.AttemptCount);
    }

    [Fact]
    public void AttemptConfirm_AfterMaxWrongAttempts_ReturnsLockedOut()
    {
        var verification = Request(maxAttempts: 2);
        verification.AttemptConfirm(Hash("000000"), Now.AddSeconds(1));
        verification.AttemptConfirm(Hash("111111"), Now.AddSeconds(2));

        var outcome = verification.AttemptConfirm(Hash("222222"), Now.AddSeconds(3));

        Assert.Equal(PhoneVerificationConfirmOutcome.LockedOut, outcome);
    }

    /// <summary>The wrong guess that pushes <see cref="PendingPhoneVerification.AttemptCount"/> to
    /// <see cref="PendingPhoneVerification.MaxAttempts"/> itself reports as <c>LockedOut</c>, not
    /// <c>WrongCode</c> - the caller learns the phone is now locked on the same call that caused
    /// it.</summary>
    [Fact]
    public void AttemptConfirm_TheWrongGuessThatReachesMaxAttempts_ReturnsLockedOutNotWrongCode()
    {
        var verification = Request(maxAttempts: 1);

        var outcome = verification.AttemptConfirm(Hash("000000"), Now.AddSeconds(1));

        Assert.Equal(PhoneVerificationConfirmOutcome.LockedOut, outcome);
    }

    [Fact]
    public void AttemptConfirm_EvenWithCorrectCode_WhenAlreadyLockedOut_ReturnsLockedOut()
    {
        var verification = Request(maxAttempts: 1, code: Code);
        verification.AttemptConfirm(Hash("000000"), Now.AddSeconds(1));

        var outcome = verification.AttemptConfirm(Hash(Code), Now.AddSeconds(2));

        Assert.Equal(PhoneVerificationConfirmOutcome.LockedOut, outcome);
    }

    [Fact]
    public void IssueProof_BeforeConfirmation_Throws()
    {
        var verification = Request();

        Assert.Throws<InvalidOperationException>(() => verification.IssueProof(Hash("proof"), Now.AddMinutes(20)));
    }

    [Fact]
    public void IssueProof_AfterConfirmation_SetsProofFields()
    {
        var verification = Request(code: Code);
        verification.AttemptConfirm(Hash(Code), Now.AddMinutes(1));
        var expiresAt = Now.AddMinutes(21);

        verification.IssueProof(Hash("proof-token"), expiresAt);

        Assert.Equal(Hash("proof-token"), verification.ProofTokenHash);
        Assert.Equal(expiresAt, verification.ProofExpiresAt);
    }

    [Fact]
    public void IsProofValid_WithTheCorrectTokenAndMatchingPhone_ReturnsTrue()
    {
        var verification = Request(code: Code, phone: Phone);
        verification.AttemptConfirm(Hash(Code), Now.AddMinutes(1));
        verification.IssueProof(Hash("proof-token"), Now.AddMinutes(21));

        Assert.True(verification.IsProofValid(Phone, Hash("proof-token"), Now.AddMinutes(2)));
    }

    [Fact]
    public void IsProofValid_WithTheWrongToken_ReturnsFalse()
    {
        var verification = Request(code: Code, phone: Phone);
        verification.AttemptConfirm(Hash(Code), Now.AddMinutes(1));
        verification.IssueProof(Hash("proof-token"), Now.AddMinutes(21));

        Assert.False(verification.IsProofValid(Phone, Hash("wrong-token"), Now.AddMinutes(2)));
    }

    /// <summary>`20-10`'s own critical security property, stated in its backlog file: "a caller must
    /// not be able to verify phone A and then book with phone B using that same token." The correct
    /// token, presented for a different phone number, is refused.</summary>
    [Fact]
    public void IsProofValid_WithTheCorrectTokenButADifferentPhone_ReturnsFalse()
    {
        var verification = Request(code: Code, phone: Phone);
        verification.AttemptConfirm(Hash(Code), Now.AddMinutes(1));
        verification.IssueProof(Hash("proof-token"), Now.AddMinutes(21));

        Assert.False(verification.IsProofValid("+79991230000", Hash("proof-token"), Now.AddMinutes(2)));
    }

    [Fact]
    public void IsProofValid_AfterProofExpiresAt_ReturnsFalse()
    {
        var verification = Request(code: Code, phone: Phone);
        verification.AttemptConfirm(Hash(Code), Now.AddMinutes(1));
        var expiresAt = Now.AddMinutes(21);
        verification.IssueProof(Hash("proof-token"), expiresAt);

        Assert.False(verification.IsProofValid(Phone, Hash("proof-token"), expiresAt));
    }

    [Fact]
    public void IsProofValid_BeforeAnyProofWasIssued_ReturnsFalse()
    {
        var verification = Request(code: Code, phone: Phone);
        verification.AttemptConfirm(Hash(Code), Now.AddMinutes(1));

        Assert.False(verification.IsProofValid(Phone, Hash("anything"), Now.AddMinutes(2)));
    }

    [Fact]
    public void IsProofValid_WhenNeverConfirmedAtAll_ReturnsFalse()
    {
        var verification = Request(code: Code, phone: Phone);

        Assert.False(verification.IsProofValid(Phone, Hash("anything"), Now.AddMinutes(2)));
    }
}
