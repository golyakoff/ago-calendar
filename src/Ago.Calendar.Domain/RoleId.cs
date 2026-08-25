using Ago.Platform.Kernel;

namespace Ago.Calendar.Domain;

public readonly record struct RoleId(Guid Value) : IStronglyTypedId;
