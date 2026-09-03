namespace Ago.Calendar.Infrastructure.Postgres;

/// <summary>
/// `22-11`: bound from `ModuleProvisioning:Secret` - the one value `Ago.Chat.*`'s own deployment and
/// this one must be independently configured to agree on before either can prove a registration call
/// to the other. See <see cref="Ago.Calendar.Application.Abstractions.IModuleProvisioningAuthenticator"/>'s
/// own remarks for why this is a plain shared secret rather than adr/0094's signed-assertion format,
/// and `secrets.md` for where this is meant to be recorded operationally (not yet added there - see
/// this item's own report).
///
/// <para><b>Why a plain bound class, not a value object in Domain.</b> This is deployment
/// configuration - a fact about which process is allowed to provision, not a fact about a tenant or a
/// registration - the identical placement <c>PublicBookingApiOptions</c>'s own remarks give for the
/// same reason: nothing here is a business rule Application could have an opinion about.</para>
/// </summary>
public sealed class ModuleProvisioningOptions
{
    public const string SectionName = "ModuleProvisioning";

    /// <summary>Never defaults to a usable value - an absent or empty secret means every provisioning
    /// call is refused (<see cref="SharedSecretModuleProvisioningAuthenticator"/> never treats an empty
    /// configured secret as matching an empty presented header), the same "nothing about turning this
    /// on can happen by omission" reasoning <c>PublicBookingApiOptions.Enabled</c>'s own remarks
    /// give.</summary>
    public string Secret { get; set; } = string.Empty;
}
