using Ago.Calendar.Application.UseCases.ChatModuleRegistration;
using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Tests.UseCases.ChatModuleRegistration;

/// <summary>`22-11`'s own fourth Done-when: this module's own half of "a registration that exists on
/// one side only is detectable" - a caller (`Ago.Chat.*`'s own reconciliation check, or an operator)
/// compares this against the chat-side row's own existence.</summary>
public sealed class GetChatModuleRegistrationStatusHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly TenantId TenantId = new(Guid.NewGuid());
    private const string Credential = "a-shared-secret-of-sixteen-plus-chars";

    [Fact]
    public async Task HandleAsync_ForATenantWithNoRegistration_ReportsNotExists()
    {
        var registrations = new FakeChatModuleRegistrationRepository();
        var handler = new GetChatModuleRegistrationStatusHandler(registrations, new FakeClock(Now));

        var status = await handler.HandleAsync(new GetChatModuleRegistrationStatus(TenantId.Value), CancellationToken.None);

        Assert.False(status.Exists);
        Assert.False(status.HasCredentialInGracePeriod);
    }

    [Fact]
    public async Task HandleAsync_ForARegisteredTenantNeverRotated_ReportsExistsWithNoGracePeriod()
    {
        var registrations = new FakeChatModuleRegistrationRepository();
        await registrations.AddAsync(
            Domain.ChatModuleRegistration.Register(TenantId, new ChatModuleCredential(Credential), Now), CancellationToken.None);
        var handler = new GetChatModuleRegistrationStatusHandler(registrations, new FakeClock(Now));

        var status = await handler.HandleAsync(new GetChatModuleRegistrationStatus(TenantId.Value), CancellationToken.None);

        Assert.True(status.Exists);
        Assert.Equal(Now, status.RegisteredAt);
        Assert.False(status.HasCredentialInGracePeriod);
    }

    [Fact]
    public async Task HandleAsync_ImmediatelyAfterARotation_ReportsAGracePeriodCredential()
    {
        var registrations = new FakeChatModuleRegistrationRepository();
        var registration = Domain.ChatModuleRegistration.Register(TenantId, new ChatModuleCredential(Credential), Now)
            .Rotate(new ChatModuleCredential("rotated-secret-of-sixteen-plus-chars-x"), Now, TimeSpan.FromMinutes(10));
        await registrations.AddAsync(registration, CancellationToken.None);
        var handler = new GetChatModuleRegistrationStatusHandler(registrations, new FakeClock(Now));

        var status = await handler.HandleAsync(new GetChatModuleRegistrationStatus(TenantId.Value), CancellationToken.None);

        Assert.True(status.Exists);
        Assert.True(status.HasCredentialInGracePeriod);
    }
}
