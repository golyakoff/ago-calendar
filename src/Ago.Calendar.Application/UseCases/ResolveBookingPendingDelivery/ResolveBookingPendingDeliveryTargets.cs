using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.UseCases.ResolveBookingPendingDelivery;

/// <summary>The one fact `Ago.Calendar.Worker`'s own fan-out consumer needs to push - see
/// <see cref="ResolveBookingPendingDeliveryTargetsHandler"/> for what it does with it.</summary>
public readonly record struct ResolveBookingPendingDeliveryTargets(
    Guid EventId, TenantId TenantId, string Status, DateTimeOffset OccurredAt, Guid CorrelationId);
