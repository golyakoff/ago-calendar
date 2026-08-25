using Ago.Platform.Kernel;

namespace Ago.Calendar.Domain;

public readonly record struct WorkerId(Guid Value) : IStronglyTypedId;
