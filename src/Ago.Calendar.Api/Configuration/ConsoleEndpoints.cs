using System.Security.Claims;
using Ago.Calendar.Api.Auth;
using Ago.Calendar.Api.Http;
using Ago.Calendar.Application.UseCases.AccessControl;
using Ago.Calendar.Application.UseCases.BookingLifecycle;
using Ago.Calendar.Application.UseCases.Configuration;
using Ago.Calendar.Application.UseCases.Contacts;
using Ago.Calendar.Application.UseCases.DeleteDayOff;
using Ago.Calendar.Application.UseCases.EditDayBoundary;
using Ago.Calendar.Contracts;
using Ago.Calendar.Domain;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Api.Configuration;

/// <summary>
/// Everything the operator console calls: tenant setup, the shared pending queue and the three
/// booking transitions, and `20-02`'s two manual day edits.
///
/// <para><b>The tenant is never in a route, a body or a query string - it comes from the token.</b>
/// Every command below reads <c>tenant_id</c> off the principal that
/// <c>OperatorIdentityClaimsTransformation</c> resolved from this product's own <c>operators</c>
/// table. A caller-supplied tenant would be a value the caller chose, and the whole permission model
/// (<c>PermissionChecker</c> filters roles by the tenant the *action* names) only holds because the
/// tenant on the principal came out of the database rather than off the wire.</para>
///
/// <para><b>One policy on the group, not one per route.</b> A route that forgot it would be
/// unauthenticated, and the failure would look like a working endpoint. <c>ClaimsPrincipalExtensions</c>
/// throws rather than returning null for exactly this case, so a route that escaped the group would
/// fail loudly on its first request rather than acting as nobody.</para>
///
/// <para><b>What is deliberately not here.</b> No delete for a calendar, a worker or a service:
/// <c>Worker.IsActive</c>'s own remarks rule out deleting a worker who has bookings, and the same
/// argument covers the rest - a booked history is what a lead card is for. Deactivation and
/// unpublishing are the reversible operations this product offers, and they are the ones on this
/// surface.</para>
/// </summary>
public static class ConsoleEndpoints
{
    public static IEndpointRouteBuilder MapConsoleEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/v1/console")
            .RequireAuthorization(CalendarClaims.OperatorPolicy);

        group.MapGet("/configuration", HandleGetConfigurationAsync).WithName("GetTenantConfiguration");
        group.MapPut("/configuration/allowed-origins", HandleSetAllowedOriginsAsync).WithName("SetAllowedOrigins");

        group.MapPost("/calendars", HandleCreateCalendarAsync).WithName("CreateCalendar");
        group.MapPut("/calendars/{calendarId:guid}", HandleUpdateCalendarAsync).WithName("UpdateCalendar");
        group.MapPost("/services", HandleCreateServiceAsync).WithName("CreateService");
        group.MapPost("/workers", HandleCreateWorkerAsync).WithName("CreateWorker");
        group.MapPost("/working-hours", HandleAddWorkingHoursAsync).WithName("AddWorkingHoursRule");

        group.MapGet("/pending-bookings", HandlePendingBookingsAsync).WithName("GetPendingBookings");
        group.MapPost("/bookings/{bookingId:guid}/reject", HandleRejectAsync).WithName("RejectBooking");
        group.MapPost("/bookings/{bookingId:guid}/cancel", HandleCancelAsync).WithName("CancelBooking");
        group.MapPost("/bookings/{bookingId:guid}/no-show", HandleNoShowAsync).WithName("MarkNoShow");

        group.MapPost("/availability/day-off", HandleDayOffAsync).WithName("DeleteDayOff");
        group.MapPost("/availability/day-boundary", HandleDayBoundaryAsync).WithName("EditDayBoundary");

        // `20-12`: the second role, moving an operator on/off it, and the tenant contacts report.
        group.MapPost("/roles", HandleCreateRoleAsync).WithName("CreateRole");
        group.MapGet("/roles", HandleListRolesAsync).WithName("ListRoles");
        group.MapGet("/operators", HandleListOperatorsAsync).WithName("ListOperators");
        group.MapPost("/operators/{operatorId:guid}/roles/{roleId:guid}", HandleGrantRoleAsync)
            .WithName("GrantOperatorRole");
        group.MapDelete("/operators/{operatorId:guid}/roles/{roleId:guid}", HandleRevokeRoleAsync)
            .WithName("RevokeOperatorRole");
        group.MapGet("/contacts", HandleContactsAsync).WithName("GetContacts");

        return app;
    }

    private static async Task<IResult> HandleGetConfigurationAsync(
        ClaimsPrincipal principal,
        GetTenantConfigurationHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(
            new GetTenantConfiguration(principal.GetOperatorId(), principal.GetTenantId()), cancellationToken);

        if (!result.IsSuccess)
        {
            return result.Error!.Value.ToProblem(httpContext);
        }

        var configuration = result.Value;
        return Results.Ok(new TenantConfigurationResponse(
            configuration.TenantName,
            configuration.PublicKey,
            configuration.AllowedOrigins,
            [
                .. configuration.Calendars.Select(calendar => new ConfiguredCalendarResponse(
                    calendar.CalendarId.Value,
                    calendar.Name,
                    calendar.TimeZone,
                    calendar.BufferMinutes,
                    calendar.IsPublished,
                    calendar.WorkerIds,
                    [
                        .. calendar.WorkingHours.Select(rule => new WorkingHoursRuleResponse(
                            rule.RuleId.Value, rule.WorkerId.Value, (int)rule.DayOfWeek, rule.StartsAt, rule.EndsAt)),
                    ])),
            ],
            [
                .. configuration.Workers.Select(worker => new ConfiguredWorkerResponse(
                    worker.WorkerId.Value, worker.DisplayName, worker.IsActive, worker.ServiceIds)),
            ],
            [
                .. configuration.Services.Select(service => new ConfiguredServiceResponse(
                    service.ServiceId.Value, service.Name, service.DurationMinutes)),
            ]));
    }

    private static async Task<IResult> HandleSetAllowedOriginsAsync(
        SetAllowedOriginsRequest request,
        ClaimsPrincipal principal,
        SetAllowedOriginsHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Results.BadRequest();
        }

        var result = await handler.HandleAsync(
            new SetAllowedOrigins(principal.GetOperatorId(), principal.GetTenantId(), request.Origins),
            cancellationToken);

        return Complete(result, httpContext);
    }

    private static async Task<IResult> HandleCreateCalendarAsync(
        CreateCalendarRequest request,
        ClaimsPrincipal principal,
        CreateCalendarHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Results.BadRequest();
        }

        var result = await handler.HandleAsync(
            new CreateCalendar(
                principal.GetOperatorId(), principal.GetTenantId(),
                request.Name, request.TimeZone, request.BufferMinutes, request.Publish),
            cancellationToken);

        // 201 with a Location, because this one genuinely creates a resource and there is a real URL
        // to point at - unlike `20-03`'s booking POST, which transitions a row that already existed
        // and says so in its own remarks.
        return result.IsSuccess
            ? Results.Created($"/api/v1/console/calendars/{result.Value.Value}", new { calendarId = result.Value.Value })
            : result.Error!.Value.ToProblem(httpContext);
    }

    private static async Task<IResult> HandleUpdateCalendarAsync(
        Guid calendarId,
        UpdateCalendarRequest request,
        ClaimsPrincipal principal,
        UpdateCalendarHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Results.BadRequest();
        }

        var result = await handler.HandleAsync(
            new UpdateCalendar(
                principal.GetOperatorId(), principal.GetTenantId(), new CalendarId(calendarId),
                request.Name, request.BufferMinutes, request.Publish),
            cancellationToken);

        return Complete(result, httpContext);
    }

    private static async Task<IResult> HandleCreateServiceAsync(
        CreateServiceRequest request,
        ClaimsPrincipal principal,
        CreateServiceHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Results.BadRequest();
        }

        var result = await handler.HandleAsync(
            new CreateService(
                principal.GetOperatorId(), principal.GetTenantId(), request.Name, request.DurationMinutes),
            cancellationToken);

        return result.IsSuccess
            ? Results.Created($"/api/v1/console/services/{result.Value.Value}", new { serviceId = result.Value.Value })
            : result.Error!.Value.ToProblem(httpContext);
    }

    private static async Task<IResult> HandleCreateWorkerAsync(
        CreateWorkerRequest request,
        ClaimsPrincipal principal,
        CreateWorkerHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Results.BadRequest();
        }

        var result = await handler.HandleAsync(
            new CreateWorker(
                principal.GetOperatorId(), principal.GetTenantId(), request.DisplayName,
                new CalendarId(request.CalendarId), request.ServiceIds ?? []),
            cancellationToken);

        return result.IsSuccess
            ? Results.Created($"/api/v1/console/workers/{result.Value.Value}", new { workerId = result.Value.Value })
            : result.Error!.Value.ToProblem(httpContext);
    }

    private static async Task<IResult> HandleAddWorkingHoursAsync(
        AddWorkingHoursRuleRequest request,
        ClaimsPrincipal principal,
        AddWorkingHoursRuleHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Results.BadRequest();
        }

        if (request.DayOfWeek is < 0 or > 6)
        {
            return new Error("configuration.invalid", "A day of week is 0 (Sunday) to 6 (Saturday).")
                .ToProblem(httpContext);
        }

        var result = await handler.HandleAsync(
            new AddWorkingHoursRule(
                principal.GetOperatorId(), principal.GetTenantId(),
                new CalendarId(request.CalendarId), new WorkerId(request.WorkerId),
                (DayOfWeek)request.DayOfWeek, request.StartsAt, request.EndsAt),
            cancellationToken);

        return result.IsSuccess
            ? Results.Created($"/api/v1/console/working-hours/{result.Value.Value}", new { ruleId = result.Value.Value })
            : result.Error!.Value.ToProblem(httpContext);
    }

    private static async Task<IResult> HandlePendingBookingsAsync(
        int? limit,
        ClaimsPrincipal principal,
        GetPendingBookingsForTenantHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(
            new GetPendingBookingsForTenant(
                principal.GetOperatorId(), principal.GetTenantId(), limit ?? GetPendingBookingsForTenantHandler.MaxLimit),
            cancellationToken);

        if (!result.IsSuccess)
        {
            return result.Error!.Value.ToProblem(httpContext);
        }

        return Results.Ok(result.Value
            .Select(row => new PendingBookingResponse(
                row.EventId.Value,
                row.CalendarId.Value,
                row.WorkerId.Value,
                row.ServiceId.Value,
                row.CustomerId.Value,
                row.StartsAt,
                row.EndsAt,
                row.LocalDate,
                row.ConfirmationDeadline,
                row.IsOverdue,
                row.Phone?.Value))
            .ToArray());
    }

    private static async Task<IResult> HandleRejectAsync(
        Guid bookingId,
        ClaimsPrincipal principal,
        RejectBookingHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken) =>
        Complete(
            await handler.HandleAsync(
                new RejectBooking(principal.GetOperatorId(), principal.GetTenantId(), new Domain.EventId(bookingId)),
                cancellationToken),
            httpContext);

    private static async Task<IResult> HandleCancelAsync(
        Guid bookingId,
        ClaimsPrincipal principal,
        CancelBookingHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken) =>
        Complete(
            await handler.HandleAsync(
                new CancelBooking(principal.GetOperatorId(), principal.GetTenantId(), new Domain.EventId(bookingId)),
                cancellationToken),
            httpContext);

    private static async Task<IResult> HandleNoShowAsync(
        Guid bookingId,
        ClaimsPrincipal principal,
        MarkNoShowHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken) =>
        Complete(
            await handler.HandleAsync(
                new MarkNoShow(principal.GetOperatorId(), principal.GetTenantId(), new Domain.EventId(bookingId)),
                cancellationToken),
            httpContext);

    private static async Task<IResult> HandleDayOffAsync(
        DayOffRequest request,
        ClaimsPrincipal principal,
        DeleteDayOffHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Results.BadRequest();
        }

        return Complete(
            await handler.HandleAsync(
                new DeleteDayOff(
                    principal.GetOperatorId(), principal.GetTenantId(),
                    new CalendarId(request.CalendarId), new WorkerId(request.WorkerId), request.LocalDate),
                cancellationToken),
            httpContext);
    }

    private static async Task<IResult> HandleDayBoundaryAsync(
        DayBoundaryRequest request,
        ClaimsPrincipal principal,
        EditDayBoundaryHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Results.BadRequest();
        }

        return Complete(
            await handler.HandleAsync(
                new EditDayBoundary(
                    principal.GetOperatorId(), principal.GetTenantId(),
                    new CalendarId(request.CalendarId), new WorkerId(request.WorkerId),
                    request.LocalDate, request.OpensAt, request.ClosesAt),
                cancellationToken),
            httpContext);
    }

    private static async Task<IResult> HandleCreateRoleAsync(
        CreateRoleRequest request,
        ClaimsPrincipal principal,
        CreateRoleHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Results.BadRequest();
        }

        Permission[] permissions;
        try
        {
            permissions = [.. (request.Permissions ?? []).Select(value => new Permission(value))];
        }
        catch (ArgumentException exception)
        {
            return AccessControlErrors.Invalid(exception.Message).ToProblem(httpContext);
        }

        var result = await handler.HandleAsync(
            new CreateRole(principal.GetOperatorId(), principal.GetTenantId(), request.Name, permissions),
            cancellationToken);

        return result.IsSuccess
            ? Results.Created($"/api/v1/console/roles/{result.Value.Value}", new { roleId = result.Value.Value })
            : result.Error!.Value.ToProblem(httpContext);
    }

    private static async Task<IResult> HandleListRolesAsync(
        ClaimsPrincipal principal,
        ListRolesForTenantHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(
            new ListRolesForTenant(principal.GetOperatorId(), principal.GetTenantId()), cancellationToken);

        if (!result.IsSuccess)
        {
            return result.Error!.Value.ToProblem(httpContext);
        }

        return Results.Ok(result.Value
            .Select(role => new RoleResponse(
                role.Id.Value, role.Name, [.. role.Permissions.Select(permission => permission.Value)]))
            .ToArray());
    }

    private static async Task<IResult> HandleListOperatorsAsync(
        ClaimsPrincipal principal,
        ListOperatorsForTenantHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(
            new ListOperatorsForTenant(principal.GetOperatorId(), principal.GetTenantId()), cancellationToken);

        if (!result.IsSuccess)
        {
            return result.Error!.Value.ToProblem(httpContext);
        }

        return Results.Ok(result.Value
            .Select(op => new OperatorResponse(
                op.Id.Value, op.DisplayName, op.IsAccountOwner, [.. op.Roles.Select(r => r.RoleId.Value)]))
            .ToArray());
    }

    private static async Task<IResult> HandleGrantRoleAsync(
        Guid operatorId,
        Guid roleId,
        ClaimsPrincipal principal,
        GrantOperatorRoleHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken) =>
        Complete(
            await handler.HandleAsync(
                new GrantOperatorRole(
                    principal.GetOperatorId(), principal.GetTenantId(),
                    new OperatorId(operatorId), new RoleId(roleId)),
                cancellationToken),
            httpContext);

    private static async Task<IResult> HandleRevokeRoleAsync(
        Guid operatorId,
        Guid roleId,
        ClaimsPrincipal principal,
        RevokeOperatorRoleHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken) =>
        Complete(
            await handler.HandleAsync(
                new RevokeOperatorRole(
                    principal.GetOperatorId(), principal.GetTenantId(),
                    new OperatorId(operatorId), new RoleId(roleId)),
                cancellationToken),
            httpContext);

    private static async Task<IResult> HandleContactsAsync(
        ClaimsPrincipal principal,
        GetTenantContactsHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(
            new GetTenantContacts(principal.GetOperatorId(), principal.GetTenantId()), cancellationToken);

        if (!result.IsSuccess)
        {
            return result.Error!.Value.ToProblem(httpContext);
        }

        return Results.Ok(result.Value
            .Select(row => new ContactResponse(
                row.CustomerId.Value,
                row.Phone.Value,
                row.DisplayName,
                row.Notes,
                row.NoShowCount,
                row.FirstSeenAt,
                row.LastSeenAt))
            .ToArray());
    }

    /// <summary>204 for a successful command that returns nothing. api-design.md's own shape, and it
    /// keeps a caller from parsing an empty body as if it meant something.</summary>
    private static IResult Complete(Result result, HttpContext httpContext) =>
        result.IsSuccess ? Results.NoContent() : result.Error!.Value.ToProblem(httpContext);
}
