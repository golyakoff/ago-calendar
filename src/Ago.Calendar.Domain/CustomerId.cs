using Ago.Platform.Kernel;

namespace Ago.Calendar.Domain;

public readonly record struct CustomerId(Guid Value) : IStronglyTypedId;
