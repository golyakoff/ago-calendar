using Ago.Calendar.Application.UseCases.ChatModuleRegistration;
using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Tests.UseCases.ChatModuleRegistration;

/// <summary>`22-11`'s own first Done-when, at the Application level: a registration that did not
/// exist can be created through this handler, and not twice.</summary>
public sealed class RegisterChatModuleHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);
    private const string ValidCredential = "a-shared-secret-of-sixteen-plus-chars";

    private sealed record Fixture(
        RegisterChatModuleHandler Handler, RecordingTenantRepository Tenants, FakeChatModuleRegistrationRepository Registrations);

    private static Fixture CreateFixture(bool tenantExists = true)
    {
        var tenants = new RecordingTenantRepository();
        if (tenantExists)
        {
            tenants.AddAsync(
                Tenant.Register(TenantId, "Barbershop", new TenantPublicKey("barbershop"), Now), CancellationToken.None);
        }

        var registrations = new FakeChatModuleRegistrationRepository();
        var handler = new RegisterChatModuleHandler(tenants, registrations, new FakeClock(Now));
        return new Fixture(handler, tenants, registrations);
    }

    private static readonly TenantId TenantId = new(Guid.NewGuid());

    [Fact]
    public async Task HandleAsync_ForAProvisionedTenantWithNoExistingRow_CreatesTheRegistration()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new RegisterChatModule(TenantId.Value, ValidCredential), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var saved = await fixture.Registrations.GetByTenantIdAsync(TenantId, CancellationToken.None);
        Assert.NotNull(saved);
        Assert.Equal(new ChatModuleCredential(ValidCredential), saved!.Credential);
        Assert.Equal(Now, saved.RegisteredAt);
    }

    [Fact]
    public async Task HandleAsync_ForATenantThatDoesNotExist_ReturnsTenantNotFound_AndWritesNothing()
    {
        var fixture = CreateFixture(tenantExists: false);

        var result = await fixture.Handler.HandleAsync(
            new RegisterChatModule(TenantId.Value, ValidCredential), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("chat_module_registration.tenant_not_found", result.Error!.Value.Code);
        Assert.Null(await fixture.Registrations.GetByTenantIdAsync(TenantId, CancellationToken.None));
    }

    /// <summary>The exact case the item's own report argues for: a second registration for a tenant
    /// that already has one is a caller mistake with its own remedy (rotate), not a silent
    /// overwrite.</summary>
    [Fact]
    public async Task HandleAsync_ForATenantThatIsAlreadyRegistered_ReturnsAlreadyRegistered_AndDoesNotOverwrite()
    {
        var fixture = CreateFixture();
        var existing = Domain.ChatModuleRegistration.Register(TenantId, new ChatModuleCredential(ValidCredential), Now);
        await fixture.Registrations.AddAsync(existing, CancellationToken.None);

        var result = await fixture.Handler.HandleAsync(
            new RegisterChatModule(TenantId.Value, "a-completely-different-secret-value"), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("chat_module_registration.already_registered", result.Error!.Value.Code);
        var stillThere = await fixture.Registrations.GetByTenantIdAsync(TenantId, CancellationToken.None);
        Assert.Equal(new ChatModuleCredential(ValidCredential), stillThere!.Credential);
    }

    [Fact]
    public async Task HandleAsync_WithATooShortCredential_ReturnsInvalidCredential()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new RegisterChatModule(TenantId.Value, "too-short"), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("chat_module_registration.invalid_credential", result.Error!.Value.Code);
        Assert.Null(await fixture.Registrations.GetByTenantIdAsync(TenantId, CancellationToken.None));
    }
}
