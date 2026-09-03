using Ago.Calendar.Application.UseCases.ChatModuleRegistration;
using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Tests.UseCases.ChatModuleRegistration;

/// <summary>`22-11`'s own second Done-when, at the Application level.</summary>
public sealed class RotateChatModuleCredentialHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly TenantId TenantId = new(Guid.NewGuid());
    private const string OriginalCredential = "original-secret-of-sixteen-plus-chars";
    private const string NewCredential = "rotated-secret-of-sixteen-plus-chars-x";

    private sealed record Fixture(RotateChatModuleCredentialHandler Handler, FakeChatModuleRegistrationRepository Registrations, FakeClock Clock);

    private static Fixture CreateFixture(bool seeded = true)
    {
        var registrations = new FakeChatModuleRegistrationRepository();
        if (seeded)
        {
            registrations.AddAsync(
                Domain.ChatModuleRegistration.Register(TenantId, new ChatModuleCredential(OriginalCredential), Now),
                CancellationToken.None);
        }

        var clock = new FakeClock(Now);
        var handler = new RotateChatModuleCredentialHandler(registrations, clock);
        return new Fixture(handler, registrations, clock);
    }

    [Fact]
    public async Task HandleAsync_ForARegisteredTenant_ReplacesTheCurrentCredential()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(new RotateChatModuleCredential(TenantId.Value, NewCredential), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var saved = await fixture.Registrations.GetByTenantIdAsync(TenantId, CancellationToken.None);
        Assert.Equal(new ChatModuleCredential(NewCredential), saved!.Credential);
    }

    /// <summary>The claim the item's own Done-when names directly: the credential just replaced still
    /// verifies immediately after rotation, not merely eventually.</summary>
    [Fact]
    public async Task HandleAsync_TheOldCredentialStillVerifiesImmediatelyAfterRotation()
    {
        var fixture = CreateFixture();

        await fixture.Handler.HandleAsync(new RotateChatModuleCredential(TenantId.Value, NewCredential), CancellationToken.None);

        var saved = await fixture.Registrations.GetByTenantIdAsync(TenantId, CancellationToken.None);
        var active = saved!.ActiveCredentials(fixture.Clock.UtcNow).ToList();
        Assert.Contains(new ChatModuleCredential(OriginalCredential), active);
        Assert.Contains(new ChatModuleCredential(NewCredential), active);
    }

    [Fact]
    public async Task HandleAsync_ForATenantWithNoRegistration_ReturnsNotFound()
    {
        var fixture = CreateFixture(seeded: false);

        var result = await fixture.Handler.HandleAsync(new RotateChatModuleCredential(TenantId.Value, NewCredential), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("chat_module_registration.not_found", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WithATooShortNewCredential_ReturnsInvalidCredential_AndLeavesTheOldOneInPlace()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(new RotateChatModuleCredential(TenantId.Value, "too-short"), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("chat_module_registration.invalid_credential", result.Error!.Value.Code);
        var saved = await fixture.Registrations.GetByTenantIdAsync(TenantId, CancellationToken.None);
        Assert.Equal(new ChatModuleCredential(OriginalCredential), saved!.Credential);
    }
}
