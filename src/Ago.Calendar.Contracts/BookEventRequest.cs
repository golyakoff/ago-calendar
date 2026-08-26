namespace Ago.Calendar.Contracts;

/// <summary>
/// The booking request's body. The calendar and the event come from the route, not from here - a
/// resource's identity belongs in its URL (api-design.md), and duplicating them in the body would
/// create a second, disagreeing copy somebody has to decide between.
/// </summary>
/// <param name="ServiceId">What the customer is booking. Exactly one in v1.</param>
/// <param name="Phone">As typed. Normalised server-side, so a customer may write it any way they
/// like and still reach the same lead card.</param>
/// <param name="DisplayName">Optional. Never overwrites a name an operator already curated.</param>
public sealed record BookEventRequest(Guid ServiceId, string Phone, string? DisplayName);
