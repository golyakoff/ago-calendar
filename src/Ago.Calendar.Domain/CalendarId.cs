using Ago.Platform.Kernel;

namespace Ago.Calendar.Domain;

public readonly record struct CalendarId(Guid Value) : IStronglyTypedId;
