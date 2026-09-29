using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.UseCases.PersonRecognition;

/// <summary>
/// `26-268`§2a/`adr/0188`: "who has already booked with this number, in this tenant" - the operator
/// types a phone first, before deciding whether to mint a new client or reuse one. See
/// <see cref="Ago.Calendar.Application.Abstractions.IPersonRecognitionReadStore"/> for why this can never
/// answer with a single "the" person.
/// </summary>
/// <param name="Phone">Raw, as the operator typed it - validated by <see cref="PhoneNumber"/>'s own
/// constructor, the identical pattern <c>EnterManualBookingHandler</c> already uses for the same
/// field.</param>
public readonly record struct GetPersonCandidatesByPhone(OperatorId OperatorId, TenantId TenantId, string Phone);
