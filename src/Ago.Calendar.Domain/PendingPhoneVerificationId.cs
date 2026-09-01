using Ago.Platform.Kernel;

namespace Ago.Calendar.Domain;

public readonly record struct PendingPhoneVerificationId(Guid Value) : IStronglyTypedId;
