using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Application.UseCases.Configuration;
using Ago.Calendar.Domain;
using Ago.Platform.Kernel;

namespace Ago.Calendar.Application.Tests;

/// <summary>
/// `26-97`: correcting and removing a working-hours rule, at the level where this item's two rules
/// live - <b>the caller holds <see cref="Permission.CalendarConfigure"/> for the tenant the action
/// names</b>, and <b>the rule belongs to that tenant</b> - plus the item's own design decision, that
/// a correction is always allowed and always reports which already-cut days it did not reach.
/// </summary>
public class WorkingHoursRuleHandlerTests
{
    private static readonly OperatorId Actor = new(new Guid("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));

    /// <summary>A Monday - see the reconciliation tests below, which count Mondays.</summary>
    private static readonly DateOnly Today = BookingFixtures.LocalDate;

    [Fact]
    public async Task UpdatingARule_RequiresCalendarConfigure_AndWritesNothingWhenRefused()
    {
        var world = new World();
        world.Permissions.Deny(Permission.CalendarConfigure);

        var result = await world.UpdateAsync(DayOfWeek.Monday, new TimeOnly(10, 0), new TimeOnly(19, 0));

        Assert.True(result.IsFailure);
        Assert.Equal("configuration.forbidden", result.Error!.Value.Code);
        Assert.Empty(world.Rules.Saved);
        // A refused permission must not even have looked the rule up: the error would otherwise tell
        // a caller with no right whether an id exists.
        Assert.Empty(world.Rules.Loaded);
    }

    [Fact]
    public async Task DeletingARule_RequiresCalendarConfigure_AndWritesNothingWhenRefused()
    {
        var world = new World();
        world.Permissions.Deny(Permission.CalendarConfigure);

        var result = await world.DeleteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("configuration.forbidden", result.Error!.Value.Code);
        Assert.Empty(world.Rules.Deleted);
        Assert.Empty(world.Rules.Loaded);
    }

    [Fact]
    public async Task ARuleOfAnotherTenant_IsReportedAsAbsent_NotAsForbidden()
    {
        // The permission check passes - this operator really does hold calendar:configure in their
        // own tenant. What stops them is the second check, against the tenant on the rule's own
        // calendar, and it is worded as "no such rule" because an operator of tenant A learning that
        // an id exists in tenant B is a cross-tenant leak however politely it is phrased.
        var world = new World();
        var stranger = new TenantId(new Guid("99999999-9999-9999-9999-999999999999"));

        var updated = await world.UpdateAsync(
            DayOfWeek.Monday, new TimeOnly(10, 0), new TimeOnly(19, 0), tenantId: stranger);
        var deleted = await world.DeleteAsync(tenantId: stranger);

        Assert.Equal("configuration.not_found", updated.Error!.Value.Code);
        Assert.Equal("configuration.not_found", deleted.Error!.Value.Code);
        Assert.Empty(world.Rules.Saved);
        Assert.Empty(world.Rules.Deleted);
    }

    [Fact]
    public async Task AnUnknownRuleId_IsReportedAsAbsent()
    {
        var world = new World();

        var result = await world.UpdateAsync(
            DayOfWeek.Monday, new TimeOnly(10, 0), new TimeOnly(19, 0),
            ruleId: new WorkingHoursRuleId(Guid.CreateVersion7(BookingFixtures.Now)));

        Assert.Equal("configuration.not_found", result.Error!.Value.Code);
    }

    [Fact]
    public async Task AnUpdate_TurnsTheSameDomainRefusalTheAddPathGivesIntoAnOrdinaryRejection()
    {
        // The rule `26-97` is held to: the edit path refuses exactly where creation refuses. A shift
        // crossing midnight is two rules on two days, and it is no more acceptable as a correction
        // than as a new rule.
        var world = new World();

        var result = await world.UpdateAsync(DayOfWeek.Friday, new TimeOnly(22, 0), new TimeOnly(2, 0));

        Assert.True(result.IsFailure);
        Assert.Equal("configuration.invalid", result.Error!.Value.Code);
        Assert.Empty(world.Rules.Saved);
    }

    [Fact]
    public async Task AnUpdate_SavesTheCorrectedRuleAndEchoesItBack()
    {
        var world = new World();

        var result = await world.UpdateAsync(DayOfWeek.Wednesday, new TimeOnly(10, 0), new TimeOnly(19, 0));

        Assert.True(result.IsSuccess);
        Assert.Equal(DayOfWeek.Wednesday, result.Value.DayOfWeek);
        Assert.Equal(new TimeOnly(19, 0), result.Value.EndsAt);

        var saved = Assert.Single(world.Rules.Saved);
        Assert.Equal(DayOfWeek.Wednesday, saved.DayOfWeek);
        Assert.Equal(new TimeOnly(10, 0), saved.StartsAt);
        Assert.Equal(new TimeOnly(19, 0), saved.EndsAt);
    }

    [Fact]
    public async Task ADelete_RemovesTheRule()
    {
        var world = new World();

        var result = await world.DeleteAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(world.Rule.Id, Assert.Single(world.Rules.Deleted).Id);
    }

    [Fact]
    public async Task WithNothingCutYet_TheCorrectionReportsNoFollowUp()
    {
        // The cursor sits at today, so the job has generated nothing from today onward: the
        // correction is complete on its own and there is no re-cut to ask anybody for.
        var world = new World(materializeFrom: Today);

        var result = await world.UpdateAsync(DayOfWeek.Monday, new TimeOnly(10, 0), new TimeOnly(19, 0));

        Assert.Null(result.Value.Reconciliation.RecutFrom);
        Assert.Empty(result.Value.Reconciliation.AlreadyCutDays);
        Assert.Equal(0, result.Value.Reconciliation.LiveBookingCount);
    }

    [Fact]
    public async Task WithDaysAlreadyCut_TheCorrectionNamesThemAndTheDateToRecutFrom()
    {
        // `26-97`'s design decision, asserted: the edit is allowed, and the days it could not reach
        // come back with it. Two weeks are already cut, so the two Mondays inside them are the days
        // still carrying the old hours.
        var world = new World(
            materializeFrom: Today.AddDays(14),
            slots:
            [
                Row(Today.AddDays(7), EventStatus.Booked),
                Row(Today.AddDays(7), EventStatus.Available),
                // A Tuesday - a real booking, on a day this Monday rule does not touch.
                Row(Today.AddDays(1), EventStatus.Booked),
            ]);

        var result = await world.UpdateAsync(DayOfWeek.Monday, new TimeOnly(10, 0), new TimeOnly(19, 0));

        var reconciliation = result.Value.Reconciliation;
        Assert.Equal(Today, reconciliation.RecutFrom);
        Assert.Equal([Today, Today.AddDays(7)], reconciliation.AlreadyCutDays);

        // One: the Booked Monday. Not the Available row beside it (nobody is holding it) and not the
        // Booked Tuesday (this change does not move Tuesday's grid).
        Assert.Equal(1, reconciliation.LiveBookingCount);
    }

    [Fact]
    public async Task MovingARuleToAnotherWeekday_ReportsBothWeekdaysAsAffected()
    {
        // Understating this was the easy bug: the days the rule is *leaving* keep their old grid just
        // as much as the days it is arriving on, and an operator told only about one of the two
        // re-cuts half of what they have to.
        var world = new World(materializeFrom: Today.AddDays(9));

        var result = await world.UpdateAsync(DayOfWeek.Wednesday, new TimeOnly(10, 0), new TimeOnly(19, 0));

        Assert.Equal(
            [Today, Today.AddDays(2), Today.AddDays(7)],
            result.Value.Reconciliation.AlreadyCutDays);
        Assert.Equal(Today, result.Value.Reconciliation.RecutFrom);
    }

    [Fact]
    public async Task ADelete_ReportsTheSameAlreadyCutDays_ComputedBeforeTheRowIsGone()
    {
        var world = new World(
            materializeFrom: Today.AddDays(14),
            slots: [Row(Today, EventStatus.PendingConfirmation)]);

        var result = await world.DeleteAsync();

        Assert.Equal(Today, result.Value.RecutFrom);
        Assert.Equal([Today, Today.AddDays(7)], result.Value.AlreadyCutDays);
        // A pending booking counts: somebody is waiting on it, exactly as RecutPreviewHandler treats it.
        Assert.Equal(1, result.Value.LiveBookingCount);
    }

    [Fact]
    public async Task OnACycleSchedule_TheresNothingToReconcile()
    {
        // A cycle schedule makes the materialiser ignore WorkingHoursRule rows entirely, so editing
        // one moves no slot anywhere and there is no re-cut to suggest - see
        // MaterializeAvailabilityHandler's own branch on WorkerSchedule.Kind.
        var world = new World(cycle: true, materializeFrom: Today.AddDays(14));

        var result = await world.UpdateAsync(DayOfWeek.Monday, new TimeOnly(10, 0), new TimeOnly(19, 0));

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value.Reconciliation.RecutFrom);
        Assert.Empty(result.Value.Reconciliation.AlreadyCutDays);
    }

    [Fact]
    public async Task WithNoScheduleAtAll_TheresNothingToReconcile()
    {
        var world = new World(withSchedule: false);

        var result = await world.DeleteAsync();

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value.RecutFrom);
        Assert.Empty(result.Value.AlreadyCutDays);
    }

    private static WorkerSlotRow Row(DateOnly localDate, EventStatus status) => new(
        new EventId(Guid.CreateVersion7(BookingFixtures.Now)),
        localDate,
        BookingFixtures.Now,
        BookingFixtures.Now.AddMinutes(45),
        status,
        null,
        null,
        null,
        null,
        false,
        null);

    private sealed class World
    {
        private readonly RecordingCalendarRepository _calendars;
        private readonly RecordingWorkerRepository _workers;
        private readonly WorkingHoursReconciler _reconciler;

        public World(
            DateOnly? materializeFrom = null,
            bool cycle = false,
            bool withSchedule = true,
            WorkerSlotRow[]? slots = null)
        {
            var calendar = BookingFixtures.Calendar();
            _calendars = new RecordingCalendarRepository(calendar);

            var worker = BookingFixtures.WorkerOffering(BookingFixtures.HaircutService());
            _workers = new RecordingWorkerRepository();
            _workers.Added.Add(worker);

            Rule = WorkingHoursRule.For(
                new WorkingHoursRuleId(new Guid("dddddddd-dddd-dddd-dddd-dddddddddddd")),
                worker, calendar, DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(9, 30));
            Rules = new RecordingWorkingHoursRuleRepository(Rule);

            WorkerSchedule? schedule = null;
            if (withSchedule)
            {
                var cursor = materializeFrom ?? Today;
                schedule = cycle
                    ? WorkerSchedule.CreateCycle(
                        BookingFixtures.WorkerScheduleId, worker.Id, Today, 2, 2,
                        new TimeOnly(9, 0), new TimeOnly(18, 0), 45, 0, 30, cursor, BookingFixtures.Now)
                    : WorkerSchedule.CreateWeekly(
                        BookingFixtures.WorkerScheduleId, worker.Id, 45, 0, 30, cursor, BookingFixtures.Now);
            }

            _reconciler = new WorkingHoursReconciler(
                new FakeWorkerScheduleRepository(schedule),
                new FakeWorkerSlotReadStore(slots ?? []),
                new FakeWallClockResolver(TimeSpan.Zero),
                new FakeClock(BookingFixtures.Now));
        }

        public WorkingHoursRule Rule { get; }

        public RecordingWorkingHoursRuleRepository Rules { get; }

        public FakePermissionChecker Permissions { get; } = new();

        public Task<Result<WorkingHoursRuleUpdated>> UpdateAsync(
            DayOfWeek dayOfWeek,
            TimeOnly startsAt,
            TimeOnly endsAt,
            TenantId? tenantId = null,
            WorkingHoursRuleId? ruleId = null) =>
            new UpdateWorkingHoursRuleHandler(_calendars, _workers, Rules, Permissions, _reconciler)
                .HandleAsync(
                    new UpdateWorkingHoursRule(
                        Actor, tenantId ?? BookingFixtures.TenantId, ruleId ?? Rule.Id,
                        dayOfWeek, startsAt, endsAt),
                    CancellationToken.None);

        public Task<Result<WorkingHoursReconciliation>> DeleteAsync(TenantId? tenantId = null) =>
            new DeleteWorkingHoursRuleHandler(_calendars, _workers, Rules, Permissions, _reconciler)
                .HandleAsync(
                    new DeleteWorkingHoursRule(Actor, tenantId ?? BookingFixtures.TenantId, Rule.Id),
                    CancellationToken.None);
    }
}

/// <summary>Records what was read and written, so "the refused call touched nothing" is an assertion
/// rather than an inference - the same shape <c>RecordingWorkerRepository</c> already uses.</summary>
internal sealed class RecordingWorkingHoursRuleRepository(WorkingHoursRule existing) : IWorkingHoursRuleRepository
{
    public List<WorkingHoursRuleId> Loaded { get; } = [];

    public List<WorkingHoursRule> Saved { get; } = [];

    public List<WorkingHoursRule> Deleted { get; } = [];

    public Task<IReadOnlyList<WorkingHoursRule>> ListForCalendarAsync(
        CalendarId calendarId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<WorkingHoursRule>>(existing.CalendarId == calendarId ? [existing] : []);

    public Task<WorkingHoursRule?> GetByIdAsync(WorkingHoursRuleId id, CancellationToken cancellationToken)
    {
        Loaded.Add(id);
        return Task.FromResult<WorkingHoursRule?>(existing.Id == id ? existing : null);
    }

    public Task AddAsync(WorkingHoursRule rule, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Not reached by the update or delete handlers.");

    public Task SaveAsync(WorkingHoursRule rule, CancellationToken cancellationToken)
    {
        Saved.Add(rule);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(WorkingHoursRule rule, CancellationToken cancellationToken)
    {
        Deleted.Add(rule);
        return Task.CompletedTask;
    }
}
