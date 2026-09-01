using Ago.Platform.Kernel;

namespace Ago.Calendar.Domain;

public readonly record struct WorkerScheduleId(Guid Value) : IStronglyTypedId;
