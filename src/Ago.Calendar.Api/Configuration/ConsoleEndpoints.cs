using System.Security.Claims;
using Ago.Calendar.Api.Auth;
using Ago.Calendar.Api.Http;
using Ago.Calendar.Application.UseCases.AccessControl;
using Ago.Calendar.Application.UseCases.BookingLifecycle;
using Ago.Calendar.Application.UseCases.Configuration;
using Ago.Calendar.Application.UseCases.Contacts;
using Ago.Calendar.Application.UseCases.DeleteDayOff;
using Ago.Calendar.Application.UseCases.EditDayBoundary;
using Ago.Calendar.Application.UseCases.RecutSchedule;
using Ago.Calendar.Application.UseCases.WorkerSlots;
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
/// <para><b>What is deliberately not here.</b> No delete for a calendar or a service - the same
/// argument <c>Worker.IsActive</c>'s own remarks make for a worker applies to both, and deactivation/
/// unpublishing are the reversible operations this product offers for them. `20-13` narrowed that
/// rule for a worker specifically: one who has never been booked carries no history worth keeping,
/// so <c>DELETE /workers/{id}</c> exists and every other worker still falls back to deactivation.</para>
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
        group.MapGet("/workers", HandleListWorkersAsync).WithName("ListWorkers");
        group.MapGet("/workers/{workerId:guid}", HandleGetWorkerAsync).WithName("GetWorker");
        group.MapPut("/workers/{workerId:guid}", HandleUpdateWorkerAsync).WithName("UpdateWorker");
        group.MapDelete("/workers/{workerId:guid}", HandleDeleteWorkerAsync).WithName("DeleteWorker");

        // `20-14`: a worker's own schedule template - weekly or cycle, slot length, buffer, horizon.
        group.MapGet("/workers/{workerId:guid}/schedule", HandleGetWorkerScheduleAsync).WithName("GetWorkerSchedule");
        group.MapPut("/workers/{workerId:guid}/schedule", HandleSaveWorkerScheduleAsync).WithName("SaveWorkerSchedule");

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

        // `20-15`: the materialised slot view - what the tenant's own schedule actually produced for
        // one worker, over a date range. Read-only; see the item's own scope for why it offers no
        // edit of its own.
        group.MapGet("/workers/{workerId:guid}/slots", HandleWorkerSlotsAsync).WithName("GetWorkerSlots");

        // `20-16`: the one deliberate, human-triggered exception to the forward-only cursor.
        group.MapPost("/workers/{workerId:guid}/schedule/recut/preview", HandleRecutPreviewAsync)
            .WithName("RecutSchedulePreview");
        group.MapPost("/workers/{workerId:guid}/schedule/recut", HandleRecutConfirmAsync)
            .WithName("RecutSchedule");

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
                request.Name, request.TimeZone, request.Publish),
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
                request.Name, request.Publish),
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
                principal.GetOperatorId(), principal.GetTenantId(),
                request.LastName, request.FirstName, request.MiddleName, request.DisplayName,
                new CalendarId(request.CalendarId), request.ServiceIds ?? []),
            cancellationToken);

        return result.IsSuccess
            ? Results.Created($"/api/v1/console/workers/{result.Value.Value}", new { workerId = result.Value.Value })
            : result.Error!.Value.ToProblem(httpContext);
    }

    private static async Task<IResult> HandleListWorkersAsync(
        ClaimsPrincipal principal,
        ListWorkersForTenantHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(
            new ListWorkersForTenant(principal.GetOperatorId(), principal.GetTenantId()), cancellationToken);

        if (!result.IsSuccess)
        {
            return result.Error!.Value.ToProblem(httpContext);
        }

        return Results.Ok(result.Value.Select(ToWorkerResponse).ToArray());
    }

    private static async Task<IResult> HandleGetWorkerAsync(
        Guid workerId,
        ClaimsPrincipal principal,
        GetWorkerHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(
            new GetWorker(principal.GetOperatorId(), principal.GetTenantId(), new WorkerId(workerId)),
            cancellationToken);

        return result.IsSuccess ? Results.Ok(ToWorkerResponse(result.Value)) : result.Error!.Value.ToProblem(httpContext);
    }

    private static async Task<IResult> HandleUpdateWorkerAsync(
        Guid workerId,
        UpdateWorkerRequest request,
        ClaimsPrincipal principal,
        UpdateWorkerHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Results.BadRequest();
        }

        return Complete(
            await handler.HandleAsync(
                new UpdateWorker(
                    principal.GetOperatorId(), principal.GetTenantId(), new WorkerId(workerId),
                    request.LastName, request.FirstName, request.MiddleName, request.DisplayName, request.IsActive),
                cancellationToken),
            httpContext);
    }

    private static async Task<IResult> HandleDeleteWorkerAsync(
        Guid workerId,
        ClaimsPrincipal principal,
        DeleteWorkerHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken) =>
        Complete(
            await handler.HandleAsync(
                new DeleteWorker(principal.GetOperatorId(), principal.GetTenantId(), new WorkerId(workerId)),
                cancellationToken),
            httpContext);

    private static async Task<IResult> HandleGetWorkerScheduleAsync(
        Guid workerId,
        ClaimsPrincipal principal,
        GetWorkerScheduleHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(
            new GetWorkerSchedule(principal.GetOperatorId(), principal.GetTenantId(), new WorkerId(workerId)),
            cancellationToken);

        return result.IsSuccess
            ? Results.Ok(ToWorkerScheduleResponse(result.Value))
            : result.Error!.Value.ToProblem(httpContext);
    }

    private static async Task<IResult> HandleSaveWorkerScheduleAsync(
        Guid workerId,
        SaveWorkerScheduleRequest request,
        ClaimsPrincipal principal,
        SaveWorkerScheduleHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Results.BadRequest();
        }

        ScheduleKind kind;
        if (string.Equals(request.Kind, "Weekly", StringComparison.OrdinalIgnoreCase))
        {
            kind = ScheduleKind.Weekly;
        }
        else if (string.Equals(request.Kind, "Cycle", StringComparison.OrdinalIgnoreCase))
        {
            kind = ScheduleKind.Cycle;
        }
        else
        {
            return new Error("configuration.invalid", $"Unknown schedule kind '{request.Kind}'; expected 'Weekly' or 'Cycle'.")
                .ToProblem(httpContext);
        }

        var result = await handler.HandleAsync(
            new SaveWorkerSchedule(
                principal.GetOperatorId(), principal.GetTenantId(), new WorkerId(workerId), kind,
                request.CycleAnchor, request.CycleWorkingDays, request.CycleRestDays,
                request.CycleStartsAt, request.CycleEndsAt,
                request.SlotMinutes, request.BufferMinutes, request.HorizonDays, request.MaterializeFrom),
            cancellationToken);

        return result.IsSuccess
            ? Results.Ok(ToWorkerScheduleResponse(result.Value))
            : result.Error!.Value.ToProblem(httpContext);
    }

    private static WorkerScheduleResponse ToWorkerScheduleResponse(WorkerScheduleDetail schedule) => new(
        schedule.ScheduleId.Value,
        schedule.WorkerId.Value,
        schedule.Kind.ToString(),
        schedule.CycleAnchor,
        schedule.CycleWorkingDays,
        schedule.CycleRestDays,
        schedule.CycleStartsAt,
        schedule.CycleEndsAt,
        schedule.SlotMinutes,
        schedule.BufferMinutes,
        schedule.HorizonDays,
        schedule.MaterializeFrom,
        schedule.CreatedAt,
        schedule.UpdatedAt);

    private static WorkerResponse ToWorkerResponse(WorkerDetail worker) => new(
        worker.WorkerId.Value,
        worker.LastName,
        worker.FirstName,
        worker.MiddleName,
        worker.DisplayName,
        worker.DisplayNameIsCustom,
        worker.IsActive,
        worker.CreatedAt,
        worker.UpdatedAt);

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

    private static async Task<IResult> HandleWorkerSlotsAsync(
        Guid workerId,
        DateOnly from,
        DateOnly to,
        ClaimsPrincipal principal,
        GetWorkerSlotsHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(
            new GetWorkerSlots(principal.GetOperatorId(), principal.GetTenantId(), new WorkerId(workerId), from, to),
            cancellationToken);

        if (!result.IsSuccess)
        {
            return result.Error!.Value.ToProblem(httpContext);
        }

        return Results.Ok(result.Value
            .Select(row => new WorkerSlotResponse(
                row.EventId.Value,
                row.LocalDate,
                (int)row.LocalDate.DayOfWeek,
                row.StartsAt,
                row.EndsAt,
                row.Status.ToString(),
                row.ServiceId?.Value,
                row.ServiceName,
                row.CustomerId?.Value,
                row.CustomerDisplayName,
                row.Phone?.Value))
            .ToArray());
    }

    private static async Task<IResult> HandleRecutPreviewAsync(
        Guid workerId,
        RecutPreviewRequest request,
        ClaimsPrincipal principal,
        RecutPreviewHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Results.BadRequest();
        }

        var result = await handler.HandleAsync(
            new RecutPreview(principal.GetOperatorId(), principal.GetTenantId(), new WorkerId(workerId), request.From),
            cancellationToken);

        if (!result.IsSuccess)
        {
            return result.Error!.Value.ToProblem(httpContext);
        }

        var preview = result.Value;
        return Results.Ok(new RecutPreviewResponse(
            [
                .. preview.Days.Select(day => new RecutDayPreviewResponse(
                    day.LocalDate,
                    day.AvailableSlotsToDelete,
                    [
                        .. day.Bookings.Select(booking => new RecutBookingPreviewResponse(
                            booking.BookingId.Value,
                            booking.StartsAt,
                            booking.EndsAt,
                            booking.Status.ToString(),
                            booking.ServiceId?.Value,
                            booking.ServiceName,
                            booking.CustomerId?.Value,
                            booking.CustomerDisplayName,
                            booking.Phone?.Value,
                            booking.CanDecide)),
                    ])),
            ],
            preview.Fingerprint));
    }

    private static async Task<IResult> HandleRecutConfirmAsync(
        Guid workerId,
        RecutConfirmRequest request,
        ClaimsPrincipal principal,
        RecutConfirmHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Results.BadRequest();
        }

        RecutBookingDecision[] decisions;
        try
        {
            decisions = [.. request.Decisions.Select(ToDecision)];
        }
        catch (ArgumentException exception)
        {
            return new Error("recut.invalid", exception.Message).ToProblem(httpContext);
        }

        var result = await handler.HandleAsync(
            new RecutConfirm(
                principal.GetOperatorId(), principal.GetTenantId(), new WorkerId(workerId),
                request.From, request.Fingerprint, decisions),
            cancellationToken);

        if (!result.IsSuccess)
        {
            return result.Error!.Value.ToProblem(httpContext);
        }

        var confirmed = result.Value;
        return Results.Ok(new RecutConfirmResponse(
            [.. confirmed.RecutDays],
            [.. confirmed.SkippedDays],
            confirmed.SlotsDeleted,
            confirmed.SlotsInserted,
            confirmed.BookingsCancelled));
    }

    private static RecutBookingDecision ToDecision(RecutDecisionRequest request)
    {
        var decision = request.Decision switch
        {
            "Cancel" => RecutDecision.Cancel,
            "Keep" => RecutDecision.Keep,
            _ => throw new ArgumentException(
                $"Unknown recut decision '{request.Decision}'; expected 'Cancel' or 'Keep'."),
        };

        return new RecutBookingDecision(new Domain.EventId(request.BookingId), decision);
    }

    /// <summary>204 for a successful command that returns nothing. api-design.md's own shape, and it
    /// keeps a caller from parsing an empty body as if it meant something.</summary>
    private static IResult Complete(Result result, HttpContext httpContext) =>
        result.IsSuccess ? Results.NoContent() : result.Error!.Value.ToProblem(httpContext);
}
