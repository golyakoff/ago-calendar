using Ago.Platform.Kernel;

namespace Ago.Calendar.Domain;

public readonly record struct ServiceId(Guid Value) : IStronglyTypedId;
