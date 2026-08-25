using Ago.Platform.Kernel;

namespace Ago.Calendar.Domain;

public readonly record struct EventId(Guid Value) : IStronglyTypedId;
