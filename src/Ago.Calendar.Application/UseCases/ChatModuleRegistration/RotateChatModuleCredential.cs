namespace Ago.Calendar.Application.UseCases.ChatModuleRegistration;

/// <summary>`22-11`: the credential this tenant's calls will be proven by from now on - see
/// <see cref="RotateChatModuleCredentialHandler"/> for why the old one keeps working for a grace
/// window rather than being discarded outright.</summary>
public sealed record RotateChatModuleCredential(Guid TenantId, string NewCredential);
