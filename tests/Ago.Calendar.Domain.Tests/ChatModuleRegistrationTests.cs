namespace Ago.Calendar.Domain.Tests;

public sealed class ChatModuleRegistrationTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Register_SetsTenantIdCredentialAndRegisteredAt()
    {
        var tenantId = new TenantId(Guid.NewGuid());
        var credential = new ChatModuleCredential("a-sufficiently-long-secret-value");

        var registration = ChatModuleRegistration.Register(tenantId, credential, Now);

        Assert.Equal(tenantId, registration.TenantId);
        Assert.Equal(credential, registration.Credential);
        Assert.Equal(Now, registration.RegisteredAt);
        Assert.Null(registration.PreviousCredential);
        Assert.Null(registration.PreviousCredentialExpiresAt);
    }

    /// <summary>`22-11`'s own "no downtime for the site being rotated" claim, at the domain level: the
    /// call that signed with the old credential a moment before rotation is still valid a moment
    /// after.</summary>
    [Fact]
    public void ActiveCredentials_ImmediatelyAfterRotation_StillAcceptsTheOldCredential()
    {
        var original = new ChatModuleCredential("original-secret-of-sixteen-plus-chars");
        var rotated = new ChatModuleCredential("rotated-secret-of-sixteen-plus-chars-x");
        var registration = ChatModuleRegistration.Register(new TenantId(Guid.NewGuid()), original, Now)
            .Rotate(rotated, Now, TimeSpan.FromMinutes(10));

        var active = registration.ActiveCredentials(Now).ToList();

        Assert.Contains(rotated, active);
        Assert.Contains(original, active);
    }

    [Fact]
    public void ActiveCredentials_AfterTheOverlapWindowExpires_NoLongerAcceptsTheOldCredential()
    {
        var original = new ChatModuleCredential("original-secret-of-sixteen-plus-chars");
        var rotated = new ChatModuleCredential("rotated-secret-of-sixteen-plus-chars-x");
        var overlap = TimeSpan.FromMinutes(10);
        var registration = ChatModuleRegistration.Register(new TenantId(Guid.NewGuid()), original, Now)
            .Rotate(rotated, Now, overlap);

        var active = registration.ActiveCredentials(Now + overlap + TimeSpan.FromSeconds(1)).ToList();

        Assert.Contains(rotated, active);
        Assert.DoesNotContain(original, active);
    }

    [Fact]
    public void Rotate_SetsTheNewCredentialAsCurrent_AndPreservesRegisteredAt()
    {
        var original = new ChatModuleCredential("original-secret-of-sixteen-plus-chars");
        var rotated = new ChatModuleCredential("rotated-secret-of-sixteen-plus-chars-x");
        var registeredAt = Now.AddDays(-3);
        var registration = ChatModuleRegistration.Register(new TenantId(Guid.NewGuid()), original, registeredAt);

        var result = registration.Rotate(rotated, Now, TimeSpan.FromMinutes(10));

        Assert.Equal(rotated, result.Credential);
        Assert.Equal(original, result.PreviousCredential);
        Assert.Equal(Now.AddMinutes(10), result.PreviousCredentialExpiresAt);
        Assert.Equal(registeredAt, result.RegisteredAt);
    }

    /// <summary>Rotating twice within one grace window does not chain a second previous credential -
    /// see <see cref="ChatModuleRegistration.Rotate"/>'s own remarks for why that is the deliberate,
    /// documented choice rather than an oversight: a second rotation keeps exactly one grace
    /// generation (the credential that was current a moment ago), never two.</summary>
    [Fact]
    public void Rotate_TwiceInARow_KeepsOnlyTheMostRecentPreviousCredential_NotTheOneBeforeThat()
    {
        var first = new ChatModuleCredential("first-secret-of-sixteen-plus-characters");
        var second = new ChatModuleCredential("second-secret-of-sixteen-plus-character");
        var third = new ChatModuleCredential("third-secret-of-sixteen-plus-characters");
        var registration = ChatModuleRegistration.Register(new TenantId(Guid.NewGuid()), first, Now)
            .Rotate(second, Now, TimeSpan.FromMinutes(10));

        var result = registration.Rotate(third, Now, TimeSpan.FromMinutes(10));

        var active = result.ActiveCredentials(Now).ToList();
        Assert.Contains(third, active);
        Assert.Contains(second, active);
        Assert.DoesNotContain(first, active);
    }
}

public sealed class ChatModuleCredentialTests
{
    [Fact]
    public void Constructor_TooShort_ThrowsArgumentException()
    {
        var tooShort = new string('a', ChatModuleCredential.MinLength - 1);

        Assert.Throws<ArgumentException>(() => new ChatModuleCredential(tooShort));
    }

    [Fact]
    public void Constructor_TooLong_ThrowsArgumentException()
    {
        var tooLong = new string('a', ChatModuleCredential.MaxLength + 1);

        Assert.Throws<ArgumentException>(() => new ChatModuleCredential(tooLong));
    }

    [Fact]
    public void Constructor_Empty_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new ChatModuleCredential(string.Empty));
    }

    [Fact]
    public void Constructor_AtExactBounds_IsAllowed()
    {
        var atMin = new string('a', ChatModuleCredential.MinLength);
        var atMax = new string('a', ChatModuleCredential.MaxLength);

        Assert.Equal(atMin, new ChatModuleCredential(atMin).Value);
        Assert.Equal(atMax, new ChatModuleCredential(atMax).Value);
    }

    [Fact]
    public void ToString_NeverPrintsTheSecret()
    {
        var credential = new ChatModuleCredential("a-sufficiently-long-secret-value");

        Assert.DoesNotContain("sufficiently-long-secret-value", credential.ToString(), StringComparison.Ordinal);
    }
}
