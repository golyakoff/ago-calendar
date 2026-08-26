using Ago.Calendar.Api.Http;
using Ago.Calendar.Application.UseCases.PublicBooking;
using Ago.Calendar.Contracts;

namespace Ago.Calendar.Api.Booking;

/// <summary>
/// The three unauthenticated reads a public embed makes before anybody can book, plus the reason they
/// are shaped the way they are.
///
/// <para><b>The tenant's public key is in the path of every one of them.</b> `5-01` found live that a
/// browser's CORS preflight carries the URL and the <c>Origin</c> header and never the request body,
/// so a tenant identified from a body cannot be identified during a preflight at all - that is why
/// AGO Chat's own visitor-session endpoint forced its two-layer design. Putting the key in the path
/// is what keeps these routes from repeating the problem: the URL alone says which tenant, which is
/// the only piece of a request a preflight has.</para>
///
/// <para><b>They are still not tenant-isolated by CORS, and nothing here pretends otherwise.</b> Every
/// one of them resolves the tenant and then compares the request's <c>Origin</c> against <i>that</i>
/// tenant's list (<c>EmbedScopeResolver</c>, <c>OriginPolicy</c>). Layer 1 is a browser convenience;
/// this is the boundary.</para>
///
/// <para><b><c>GET</c>, and therefore cacheable-looking, and therefore explicitly not cached.</b>
/// Availability changes on every booking, and a response served from an intermediary would offer a
/// customer a slot that was taken minutes ago. The <c>Cache-Control</c> header below says so rather
/// than relying on the absence of one.</para>
/// </summary>
public static class PublicBookingEndpoints
{
    /// <summary>What the widget asks for. Well above what one screen shows and well below what a
    /// numbered list on a text channel could survive - see <c>GetOpenSlotsHandler.MaxLimit</c>, which
    /// is the ceiling this is clamped to.</summary>
    public const int DefaultSlotLimit = 60;

    public static IEndpointRouteBuilder MapPublicBookingEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // No per-endpoint CORS attribute: the middleware is wired globally in Program.cs and asks
        // TenantOriginCorsPolicyProvider about every request, because the answer depends on the
        // Origin header rather than on the route.
        var group = app.MapGroup("/api/v1/embed/{publicKey}").AllowAnonymous();

        group.MapGet("/", HandleSurfaceAsync).WithName("GetBookingSurface");
        group.MapGet("/calendars/{calendarId:guid}/workers", HandleWorkersAsync).WithName("GetBookableWorkers");
        group.MapGet("/calendars/{calendarId:guid}/slots", HandleOpenSlotsAsync).WithName("GetOpenSlots");

        return app;
    }

    private static async Task<IResult> HandleSurfaceAsync(
        string publicKey,
        GetBookingSurfaceHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(
            new GetBookingSurface(publicKey, OriginOf(httpContext)), cancellationToken);

        if (!result.IsSuccess)
        {
            return result.Error!.Value.ToProblem(httpContext);
        }

        NoStore(httpContext);
        var surface = result.Value;
        return Results.Ok(new BookingSurfaceResponse(
            surface.TenantName,
            [
                .. surface.Calendars.Select(calendar => new BookableCalendarResponse(
                    calendar.CalendarId.Value,
                    calendar.Name,
                    calendar.TimeZone,
                    [
                        .. calendar.Services.Select(service => new BookableServiceResponse(
                            service.ServiceId.Value, service.Name, service.DurationMinutes)),
                    ])),
            ]));
    }

    private static async Task<IResult> HandleWorkersAsync(
        string publicKey,
        Guid calendarId,
        Guid serviceId,
        GetBookableWorkersHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(
            new GetBookableWorkers(publicKey, calendarId, serviceId, OriginOf(httpContext)), cancellationToken);

        if (!result.IsSuccess)
        {
            return result.Error!.Value.ToProblem(httpContext);
        }

        NoStore(httpContext);
        return Results.Ok(result.Value
            .Select(worker => new BookableWorkerResponse(worker.WorkerId.Value, worker.DisplayName))
            .ToArray());
    }

    private static async Task<IResult> HandleOpenSlotsAsync(
        string publicKey,
        Guid calendarId,
        Guid serviceId,
        Guid? workerId,
        int? limit,
        GetOpenSlotsHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(
            new GetOpenSlots(
                publicKey, calendarId, serviceId, workerId, limit ?? DefaultSlotLimit, OriginOf(httpContext)),
            cancellationToken);

        if (!result.IsSuccess)
        {
            return result.Error!.Value.ToProblem(httpContext);
        }

        NoStore(httpContext);
        return Results.Ok(result.Value
            .Select(slot => new OpenSlotResponse(
                slot.EventId.Value,
                slot.WorkerId.Value,
                slot.WorkerDisplayName,
                slot.StartsAt,
                slot.EndsAt,
                slot.LocalDate))
            .ToArray());
    }

    /// <summary>
    /// Null when the header is absent, which is a different thing from an empty string and is treated
    /// differently - <c>OriginPolicy</c> explains why a missing <c>Origin</c> is not a rejection here
    /// and would be one in AGO Chat.
    /// </summary>
    internal static string? OriginOf(HttpContext httpContext)
    {
        var origin = httpContext.Request.Headers.Origin.ToString();
        return string.IsNullOrWhiteSpace(origin) ? null : origin;
    }

    private static void NoStore(HttpContext httpContext) =>
        httpContext.Response.Headers.CacheControl = "no-store";
}
