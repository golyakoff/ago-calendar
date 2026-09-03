using Ago.Calendar.Application.UseCases.ChatModuleRegistration;
using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Tests.UseCases.ChatModuleRegistration;

/// <summary>`22-11`'s own third Done-when, at the Application level.</summary>
public sealed class RevokeChatModuleRegistrationHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly TenantId TenantId = new(Guid.NewGuid());
    private const string Credential = "a-shared-secret-of-sixteen-plus-chars";

    [Fact]
    public async Task HandleAsync_ForARegisteredTenant_RemovesTheRow()
    {
        var registrations = new FakeChatModuleRegistrationRepository();
        await registrations.AddAsync(
            Domain.ChatModuleRegistration.Register(TenantId, new ChatModuleCredential(Credential), Now), CancellationToken.None);
        var handler = new RevokeChatModuleRegistrationHandler(registrations);

        var result = await handler.HandleAsync(new RevokeChatModuleRegistration(TenantId.Value), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Null(await registrations.GetByTenantIdAsync(TenantId, CancellationToken.None));
    }

    [Fact]
    public async Task HandleAsync_ForATenantWithNoRegistration_ReturnsNotFound()
    {
        var registrations = new FakeChatModuleRegistrationRepository();
        var handler = new RevokeChatModuleRegistrationHandler(registrations);

        var result = await handler.HandleAsync(new RevokeChatModuleRegistration(TenantId.Value), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("chat_module_registration.not_found", result.Error!.Value.Code);
    }
}
