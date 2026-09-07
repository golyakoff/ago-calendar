using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.UseCases.TenantErasure;

/// <summary>`22-30`: "erase everything this product holds for this tenant, and prove it." Carries
/// nothing but the id - unlike `RegisterChatModule`, there is no credential and no display name to
/// validate, because destroying a tenant needs no new fact about it, only the fact it already
/// is.</summary>
public sealed record EraseTenantData(TenantId TenantId);
