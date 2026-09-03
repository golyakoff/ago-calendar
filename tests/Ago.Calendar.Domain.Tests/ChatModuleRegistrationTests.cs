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
