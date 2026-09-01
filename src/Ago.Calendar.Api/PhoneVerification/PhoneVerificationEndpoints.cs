using Ago.Calendar.Api.Booking;
using Ago.Calendar.Api.Http;
using Ago.Calendar.Api.PublicBookingApi;
using Ago.Calendar.Application.UseCases.PhoneVerification;
using Ago.Calendar.Contracts;
using Ago.Calendar.Domain;

namespace Ago.Calendar.Api.PhoneVerification;

/// <summary>
/// `20-10`: the public widget's own phone-verification round trip -
/// <c>POST /api/v1/calendars/{calendarId}/phone-verifications</c> and
/// <c>POST /api/v1/calendars/{calendarId}/phone-verifications/{id}/confirm</c> - the only HTTP surface
/// that reaches <see cref="InitiatePhoneVerificationHandler"/>/<see cref="ConfirmPhoneVerificationHandler"/>.
///
/// <para><b>The calendar id in the route, not a tenant public key</b> - the identical trust boundary
/// <see cref="Ago.Calendar.Api.Booking.BookingEndpoints"/>'s own <c>POST .../book</c> uses (calendar ->
/// tenant -> origin check), rather than <c>PublicBookingEndpoints</c>'s own <c>/api/v1/embed/{publicKey}</c>
/// shape. Deliberate: this verification exists to unlock exactly one calendar's own booking call, and
/// the widget already knows which calendar it is booking against by the point it asks for a phone
/// verification.</para>
///
/// <para><b>Unauthenticated, on purpose</b> - the identical story <c>BookingEndpoints</c>'s own remarks
/// give: there is no token to check and nothing to check it against, so the calendar id binds every
/// query and the two-layer CORS check (layer 1 in <c>Program.cs</c>, layer 2 here via
/// <c>OriginPolicy</c>, applied inside the handlers themselves) is what stands in for it.</para>
/// </summary>
public static class PhoneVerificationEndpoints
{
    public static IEndpointRouteBuilder MapPhoneVerificationEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/v1/calendars/{calendarId:guid}/phone-verifications")
            .AllowAnonymous()
            // 2026-09-01: this round trip exists only to serve BookingEndpoints's own POST .../book,
            // so it is closed by the identical gate and for the identical reason - see
            // PublicBookingApiGate's own remarks.
            .AddEndpointFilter<PublicBookingApiGate>();

        group.MapPost("", HandleInitiateAsync).WithName("InitiatePhoneVerification");
        group.MapPost("/{pendingPhoneVerificationId:guid}/confirm", HandleConfirmAsync)
            .WithName("ConfirmPhoneVerification");

        return app;
    }

    private static async Task<IResult> HandleInitiateAsync(
        Guid calendarId,
        InitiatePhoneVerificationRequest request,
        InitiatePhoneVerificationHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Results.BadRequest();
        }

        var result = await handler.HandleAsync(
            new InitiatePhoneVerification(
                new CalendarId(calendarId), request.Phone ?? string.Empty,
                PublicBookingEndpoints.OriginOf(httpContext), CallerIpOf(httpContext)),
            cancellationToken);

        return result.IsFailure
            ? result.Error!.Value.ToProblem(httpContext)
            : Results.Created(
                $"/api/v1/calendars/{calendarId}/phone-verifications/{result.Value.PendingPhoneVerificationId}",
                new InitiatedPhoneVerificationResponse(
                    result.Value.PendingPhoneVerificationId, result.Value.ExpiresAt, result.Value.DeliveryMethod));
    }

    private static async Task<IResult> HandleConfirmAsync(
        Guid calendarId,
        Guid pendingPhoneVerificationId,
        ConfirmPhoneVerificationRequest request,
        ConfirmPhoneVerificationHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Results.BadRequest();
        }

        var result = await handler.HandleAsync(
            new ConfirmPhoneVerification(
                new CalendarId(calendarId), pendingPhoneVerificationId, request.Code ?? string.Empty,
                PublicBookingEndpoints.OriginOf(httpContext)),
            cancellationToken);

        return result.IsFailure
            ? result.Error!.Value.ToProblem(httpContext)
            : Results.Ok(new ConfirmedPhoneVerificationResponse(
                result.Value.PendingPhoneVerificationId, result.Value.ProofToken, result.Value.ProofExpiresAt));
    }

    /// <summary>Null when unavailable (a test host with no real connection), the same "null is a
    /// different thing from empty, and is not itself a rejection" shape
    /// <see cref="PublicBookingEndpoints.OriginOf"/> already establishes for <c>Origin</c>.</summary>
    private static string? CallerIpOf(HttpContext httpContext) =>
        httpContext.Connection.RemoteIpAddress?.ToString();
}
