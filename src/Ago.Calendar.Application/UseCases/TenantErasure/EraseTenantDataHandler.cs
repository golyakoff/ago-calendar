using Ago.Calendar.Application.Abstractions;

namespace Ago.Calendar.Application.UseCases.TenantErasure;

/// <summary>`22-30`: a thin pass-through to <see cref="ITenantErasureRepository"/>, the same
/// bare-value-return shape <see cref="ChatModuleRegistration.GetChatModuleRegistrationStatusHandler"/>
/// already uses for a call that cannot fail in a way this layer has anything to say about - there is
/// no permission to check (the provisioning secret at the API boundary is the entire access-control
/// story, `ModuleRegistrationEndpoints`'s own remarks) and no input to validate beyond the id the
/// route itself already parsed. A handler exists at all only for the same reason every other module
/// registration operation gets one: so `Ago.Calendar.Api` depends on `Ago.Calendar.Application`, never
/// on `Ago.Calendar.Infrastructure.Postgres` directly (the dependency rule) - the alternative, wiring
/// <see cref="ITenantErasureRepository"/> straight into the endpoint, would let the Api layer reach
/// past Application into an Infrastructure abstraction with nothing in between to hold a future
/// business rule.</summary>
public sealed class EraseTenantDataHandler(ITenantErasureRepository erasure)
{
    public Task<TenantErasureResult> HandleAsync(EraseTenantData command, CancellationToken cancellationToken) =>
        erasure.EraseAsync(command.TenantId, cancellationToken);
}
