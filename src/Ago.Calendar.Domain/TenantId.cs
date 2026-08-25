using Ago.Platform.Kernel;

namespace Ago.Calendar.Domain;

public readonly record struct TenantId(Guid Value) : IStronglyTypedId;
