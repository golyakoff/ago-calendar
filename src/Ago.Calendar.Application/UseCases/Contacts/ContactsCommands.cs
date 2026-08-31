using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.UseCases.Contacts;

/// <summary>The tenant contacts report. One tenant, every customer lead card it holds.</summary>
public readonly record struct GetTenantContacts(OperatorId OperatorId, TenantId TenantId);
