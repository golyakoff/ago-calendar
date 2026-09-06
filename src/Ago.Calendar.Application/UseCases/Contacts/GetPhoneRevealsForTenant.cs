using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.UseCases.Contacts;

/// <param name="Before">Keyset cursor - the last <c>Id</c> from the previous page, or
/// <see langword="null"/> for the first page.</param>
/// <param name="Limit"><see langword="null"/> means <see cref="GetPhoneRevealsForTenantHandler.DefaultLimit"/>.</param>
public readonly record struct GetPhoneRevealsForTenant(
    OperatorId OperatorId, TenantId TenantId, Guid? Before, int? Limit);
