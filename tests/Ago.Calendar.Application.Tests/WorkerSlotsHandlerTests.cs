using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Application.UseCases.WorkerSlots;
using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Tests;

/// <summary>
/// `20-15`'s own handler, every port faked: the permission ordering, the two-layer gate `20-12`'s own
/// shape reuses, and the range/existence checks. Whether the read store's SQL actually splits on
/// <c>customers</c> the way <c>PendingBookingReadStore</c>'s own two constants do is proven against a
/// real Postgres in <c>Ago.Calendar.Integration.Tests.WorkerSlotsTests</c>; this is only about the
/// handler's own decisions, which a fake proves in microseconds.
/// </summary>
public class WorkerSlotsHandlerTests
{
    private static readonly TenantId TenantId = new(new Guid("11111111-1111-1111-1111-111111111111"));
    private static readonly OperatorId Caller = new(new Guid("22222222-2222-2222-2222-222222222222"));
    private static readonly WorkerId TargetWorker = new(new Guid("33333333-3333-3333-3333-333333333333"));
    private static readonly DateOnly From = new(2026, 5, 4);
    private static readonly DateOnly To = new(2026, 5, 18);
    private static readonly DateTimeOffset Now = new(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ACallerHoldingCustomerRead_AsksTheReadStoreToIncludeContactData()
    {
        var world = new World();

        var result = await world.SlotsAsync();

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.True(Assert.Single(world.Slots.AskedFor).IncludeContactData);
    }

    [Fact]
    public async Task ACallerWithoutCustomerRead_StillSeesTheSlots_ButAsksTheReadStoreToOmitContactData()
    {
        var world = new World();
        world.Permissions.Deny(Permission.CustomerRead);

        var result = await world.SlotsAsync();

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.False(Assert.Single(world.Slots.AskedFor).IncludeContactData);
    }

    [Fact]
    public async Task ACallerWithoutCalendarConfigure_IsRefused_RegardlessOfCustomerRead()
    {
        // CalendarConfigure gates the screen itself; CustomerRead only ever adds or withholds the
        // contact columns on rows the caller was already allowed to see - the item's own Decided
        // section, mirrored from `20-12`'s identical split on the pending queue.
        var world = new World();
        world.Permissions.Deny(Permission.CalendarConfigure);

        var result = await world.SlotsAsync();

        Assert.Equal("worker_slots.forbidden", result.Error!.Value.Code);

        // Refused before the worker was even looked up, and before the read store was asked anything -
        // a caller with no right does not learn whether the worker id exists, and costs the database
        // nothing.
        Assert.Empty(world.Workers.Loaded);
        Assert.Empty(world.Slots.AskedFor);
    }

    [Fact]
    public async Task AnInvertedRange_IsRefused_BeforeTheWorkerIsLookedUp()
    {
        var world = new World();

        var result = await world.SlotsAsync(from: To, to: From);

        Assert.Equal("worker_slots.invalid_range", result.Error!.Value.Code);
        Assert.Empty(world.Workers.Loaded);
        Assert.Empty(world.Slots.AskedFor);
    }

    [Fact]
    public async Task AWorkerFromAnotherTenant_IsReportedAsNotFound_NeverAsForbidden()
    {
        var world = new World(Worker.Create(
            TargetWorker, new TenantId(Guid.NewGuid()), "Elses", "Someone", null, Now));

        var result = await world.SlotsAsync();

        Assert.Equal("worker_slots.worker_not_found", result.Error!.Value.Code);
        Assert.Empty(world.Slots.AskedFor);
    }

    [Fact]
    public async Task AMissingWorker_IsReportedAsNotFound()
    {
        var world = new World(workerExists: false);

        var result = await world.SlotsAsync();

        Assert.Equal("worker_slots.worker_not_found", result.Error!.Value.Code);
        Assert.Empty(world.Slots.AskedFor);
    }

    [Fact]
    public async Task TheReadStoreIsAskedForExactlyTheRequestedTenantWorkerAndRange()
    {
        var world = new World();

        await world.SlotsAsync();

        var asked = Assert.Single(world.Slots.AskedFor);
        Assert.Equal(TenantId, asked.TenantId);
        Assert.Equal(TargetWorker, asked.WorkerId);
        Assert.Equal(From, asked.From);
        Assert.Equal(To, asked.To);
    }

    private sealed class World
    {
        private readonly GetWorkerSlotsHandler _handler;

        /// <param name="worker">A worker to seed the lookup with. Ignored when
        /// <paramref name="workerExists"/> is <see langword="false"/>, which is the only way to test
        /// "no such worker at all" - a null default here would be indistinguishable from "the caller
        /// forgot to override it".</param>
        /// <param name="workerExists">Defaults to true and seeds the tenant's own worker, so every
        /// test but the one about a missing worker gets a real, matching one for free.</param>
        public World(Worker? worker = null, bool workerExists = true)
        {
            Workers = new WorkerLookup(workerExists
                ? worker ?? Worker.Create(TargetWorker, TenantId, "Alexeyev", "Alex", null, Now)
                : null);
            _handler = new GetWorkerSlotsHandler(Slots, Workers, Permissions);
        }

        public FakeWorkerSlotReadStore Slots { get; } = new();

        public WorkerLookup Workers { get; }

        public FakePermissionChecker Permissions { get; } = new();

        public Task<Ago.Platform.Kernel.Result<IReadOnlyList<WorkerSlotRow>>> SlotsAsync(
            DateOnly? from = null, DateOnly? to = null) =>
            _handler.HandleAsync(
                new GetWorkerSlots(Caller, TenantId, TargetWorker, from ?? From, to ?? To), CancellationToken.None);
    }
}

/// <summary>The slot view, faked. <see cref="AskedFor"/> is what proves the tenant/worker/range are
/// forwarded verbatim and what `20-12`'s own handler tests assert against, adapted here: the contact-
/// visibility decision is made once, in the handler, and handed to the read store rather than
/// re-decided there.</summary>
internal sealed class FakeWorkerSlotReadStore(params WorkerSlotRow[] rows) : IWorkerSlotReadStore
{
    public List<(TenantId TenantId, WorkerId WorkerId, DateOnly From, DateOnly To, bool IncludeContactData)> AskedFor { get; } = [];

    public Task<IReadOnlyList<WorkerSlotRow>> GetForWorkerAsync(
        TenantId tenantId, WorkerId workerId, DateOnly from, DateOnly to, bool includeContactData,
        CancellationToken cancellationToken)
    {
        AskedFor.Add((tenantId, workerId, from, to, includeContactData));
        return Task.FromResult<IReadOnlyList<WorkerSlotRow>>(rows);
    }
}

/// <summary>
/// A minimal <see cref="IWorkerRepository"/> fake private to this file rather than a reuse of
/// <c>BookingFakes.cs</c>'s own <c>FakeWorkerRepository</c> - that one is shared by several other
/// handlers' test suites already, and this one needs a <see cref="Loaded"/> log those do not, to
/// prove a forbidden or shape-invalid call never reaches the repository at all. A one-off type here
/// costs nothing and cannot destabilise tests this item never touches.
/// </summary>
internal sealed class WorkerLookup(Worker? worker) : IWorkerRepository
{
    public List<WorkerId> Loaded { get; } = [];

    public Task<Worker?> GetByIdAsync(WorkerId id, CancellationToken cancellationToken)
    {
        Loaded.Add(id);
        return Task.FromResult(worker is not null && worker.Id == id ? worker : null);
    }

    public Task<IReadOnlyList<Worker>> ListActiveForCalendarAsync(CalendarId calendarId, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Not reached by GetWorkerSlotsHandler.");

    public Task<IReadOnlyList<Worker>> ListForTenantAsync(TenantId tenantId, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Not reached by GetWorkerSlotsHandler.");

    public Task AddAsync(Worker worker, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Not reached by GetWorkerSlotsHandler.");

    public Task SaveAsync(Worker worker, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Not reached by GetWorkerSlotsHandler.");

    public Task<bool> DeleteIfNeverBookedAsync(WorkerId id, TenantId tenantId, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Not reached by GetWorkerSlotsHandler.");
}
