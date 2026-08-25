using Ago.Platform.Kernel;

namespace Ago.Calendar.Domain;

/// <summary>
/// This product's own operator identity, never <c>Ago.Chat.Domain.OperatorId</c> (adr/0027). The two
/// name the same human being and never the same row; Keycloak's <c>sub</c> is the only thing they
/// share, resolved separately by each product against its own <c>external_subject_id</c>.
/// </summary>
public readonly record struct OperatorId(Guid Value) : IStronglyTypedId;
