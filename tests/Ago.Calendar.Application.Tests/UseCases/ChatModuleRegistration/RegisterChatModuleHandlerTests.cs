using Ago.Calendar.Application.UseCases.ChatModuleRegistration;
using Microsoft.Extensions.Logging.Abstractions;
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
        var handler = new RegisterChatModuleHandler(
            tenants, registrations, new FakeClock(Now), NullLogger<RegisterChatModuleHandler>.Instance);
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

    /// <summary>`22-17`: before this item, a tenant absent from this product's own database refused
    /// the whole call with <c>chat_module_registration.tenant_not_found</c> - see this test's own
    /// git history (and this item's report) for the captured pre-fix failure text. This is exactly the
    /// gap that made the platform owner's own grant (and self-service, for a first-time tenant)
    /// impossible to complete end to end: nothing in Production ever calls the dev-only tenant
    /// provisioning route, so a chat-originated tenant id with no prior calendar row had no way to
    /// ever get one.</summary>
    [Fact]
    public async Task HandleAsync_ForATenantThatDoesNotExist_ProvisionsTheTenant_AndCreatesTheRegistration()
    {
        var fixture = CreateFixture(tenantExists: false);

        var result = await fixture.Handler.HandleAsync(
            new RegisterChatModule(TenantId.Value, ValidCredential, DisplayName: "Prospect Barbershop"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var tenant = await fixture.Tenants.GetByIdAsync(TenantId, CancellationToken.None);
        Assert.NotNull(tenant);
        Assert.Equal("Prospect Barbershop", tenant!.Name);
        // `22-17`'s own answer to "is an auto-provisioned tenant distinguishable from a real one":
        // yes, on the row itself.
        Assert.True(tenant.AutoProvisioned);
        var saved = await fixture.Registrations.GetByTenantIdAsync(TenantId, CancellationToken.None);
        Assert.NotNull(saved);
    }

    /// <summary>No display name supplied - the fallback still produces a valid, non-blank
    /// <see cref="Tenant.Name"/> rather than propagating a caller's omission into a broken row.</summary>
    [Fact]
    public async Task HandleAsync_ForATenantThatDoesNotExist_WithNoDisplayName_StillProvisions()
    {
        var fixture = CreateFixture(tenantExists: false);

        var result = await fixture.Handler.HandleAsync(
            new RegisterChatModule(TenantId.Value, ValidCredential), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var tenant = await fixture.Tenants.GetByIdAsync(TenantId, CancellationToken.None);
        Assert.NotNull(tenant);
        Assert.False(string.IsNullOrWhiteSpace(tenant!.Name));
    }

    /// <summary>Re-registering after the tenant already exists (whether auto-provisioned by an
    /// earlier call or provisioned some other way) does not re-provision or touch the tenant row -
    /// only <c>AlreadyRegistered</c> from the existing-registration check fires.</summary>
    [Fact]
    public async Task HandleAsync_ForATenantThatAlreadyExists_DoesNotReProvisionIt()
    {
        var fixture = CreateFixture();

        await fixture.Handler.HandleAsync(new RegisterChatModule(TenantId.Value, ValidCredential), CancellationToken.None);

        Assert.Equal("Barbershop", fixture.Tenants.Registered!.Name);
        // A human-registered tenant (this fixture's own seed, Tenant.Register) is never turned into
        // an auto-provisioned one by a later registration call - the flag is set once, at
        // construction, and this handler never touches an existing Tenant row at all.
        Assert.False(fixture.Tenants.Registered!.AutoProvisioned);
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
