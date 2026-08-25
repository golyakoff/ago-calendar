using Ago.Platform.Kernel;

namespace Ago.Calendar.Domain;

public readonly record struct WorkingHoursRuleId(Guid Value) : IStronglyTypedId;
