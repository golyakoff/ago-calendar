using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.UseCases.ChatModuleRegistration;

public sealed class GetChatModuleRegistrationStatusHandler(IChatModuleRegistrationRepository registrations, IClock clock)
{
    public async Task<ChatModuleRegistrationStatus> HandleAsync(
        GetChatModuleRegistrationStatus query, CancellationToken cancellationToken)
    {
        var registration = await registrations.GetByTenantIdAsync(new TenantId(query.TenantId), cancellationToken);
        if (registration is null)
        {
            return new ChatModuleRegistrationStatus(Exists: false, default, HasCredentialInGracePeriod: false);
        }

        var hasGrace = registration.ActiveCredentials(clock.UtcNow).Count() > 1;
        return new ChatModuleRegistrationStatus(Exists: true, registration.RegisteredAt, hasGrace);
    }
}
