using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Application.UseCases.ErasePerson;
using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Tests.UseCases.ErasePerson;

/// <summary>
/// `26-275`/`adr/0189`, at the Application level. The guard's own atomicity (the anonymising update
/// and the guarded delete living in one transaction) is SQL/transaction behaviour a fake proves
/// nothing about - <c>PersonEraseStoreTests</c> in <c>Ago.Calendar.Integration.Tests</c> is where that
/// is proven against a real Postgres, the identical split <c>ManualBookingStoreTests</c>'s own remarks
/// state. What this suite proves is the handler's own three-way branching around whatever the store
/// reports: forbidden, blocked, not-found, or erased - and that permission is checked before anything
/// else is even read (<c>DeleteWorkerHandler</c>'s own ordering, restated here).
/// </summary>
public sealed class ErasePersonHandlerTests
{
    private static readonly TenantId TenantId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = OperatorId.FromExternalSubjectId("kc-operator-1");
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid PersonId = Guid.NewGuid();

    [Fact]
    public async Task HandleAsync_WhenPermissionDenied_ReturnsForbidden_AndNeverReachesTheStore()
    {
        var permissions = new FakePermissionChecker();
        permissions.Deny(Permission.CustomerErase);
        var store = new FakePersonEraseStore(erases: true);
        var handler = new ErasePersonHandler(
            new FakePersonRecordRepository(SeededPerson()), store, permissions, new FakeIdGenerator(), new FakeClock(Now));

        var result = await handler.HandleAsync(new global::Ago.Calendar.Application.UseCases.ErasePerson.ErasePerson(OperatorId, TenantId, PersonId), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("person_erase.forbidden", result.Error!.Value.Code);
        // A caller with no right never learns whether a person with this id exists in this tenant at
        // all - the store is never even asked.
        Assert.Empty(store.Attempts);
    }

    [Fact]
    public async Task HandleAsync_WhenNoSuchPersonInThisTenant_ReturnsNotFound_AndNeverReachesTheStore()
    {
        var permissions = new FakePermissionChecker();
        var store = new FakePersonEraseStore(erases: true);
        var handler = new ErasePersonHandler(
            new FakePersonRecordRepository(record: null), store, permissions, new FakeIdGenerator(), new FakeClock(Now));

        var result = await handler.HandleAsync(new global::Ago.Calendar.Application.UseCases.ErasePerson.ErasePerson(OperatorId, TenantId, PersonId), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("person_erase.not_found", result.Error!.Value.Code);
        Assert.Empty(store.Attempts);
    }

    [Fact]
    public async Task HandleAsync_WhenThePersonBelongsToAnotherTenant_ReadsAsNotFound_NeverAsForbiddenOrLeaked()
    {
        // The identical cross-tenant info-hiding shape ContactsErrors.CustomerNotFound and
        // BookingLifecycleErrors.WrongTenant both already establish: an operator of tenant A must not
        // learn that a person id is real but belongs to tenant B.
        var otherTenantsPerson = PersonRecord.Register(PersonId, new TenantId(Guid.NewGuid()), Phone(), Now);
        var permissions = new FakePermissionChecker();
        var store = new FakePersonEraseStore(erases: true);
        var handler = new ErasePersonHandler(
            new FakePersonRecordRepository(otherTenantsPerson), store, permissions, new FakeIdGenerator(), new FakeClock(Now));

        var result = await handler.HandleAsync(new global::Ago.Calendar.Application.UseCases.ErasePerson.ErasePerson(OperatorId, TenantId, PersonId), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("person_erase.not_found", result.Error!.Value.Code);
        Assert.Empty(store.Attempts);
    }

    [Fact]
    public async Task HandleAsync_WhenTheStoreReportsAFutureBooking_ReturnsFutureBookingsExist()
    {
        var permissions = new FakePermissionChecker();
        var store = new FakePersonEraseStore(erases: false);
        var handler = new ErasePersonHandler(
            new FakePersonRecordRepository(SeededPerson()), store, permissions, new FakeIdGenerator(), new FakeClock(Now));

        var result = await handler.HandleAsync(new global::Ago.Calendar.Application.UseCases.ErasePerson.ErasePerson(OperatorId, TenantId, PersonId), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("person_erase.future_bookings", result.Error!.Value.Code);
        // The store was genuinely asked - this is not the earlier not-found short-circuit.
        Assert.Single(store.Attempts);
    }

    /// <summary>The other reason the guarded statement can report zero rows affected: a concurrent
    /// second erasure already removed the row between this handler's own existence check and the
    /// store's own attempt. The re-read that follows a refused attempt has to tell this apart from a
    /// live future booking - <c>DeleteWorkerHandler</c>'s own disambiguation, restated for this
    /// handler.</summary>
    [Fact]
    public async Task HandleAsync_WhenThePersonIsAlreadyGoneByTheTimeTheStoreRuns_ReturnsNotFound_NotFutureBookings()
    {
        var permissions = new FakePermissionChecker();
        var store = new FakePersonEraseStore(erases: false);
        var handler = new ErasePersonHandler(
            new OnceThenGoneFakePersonRecordRepository(SeededPerson()), store, permissions,
            new FakeIdGenerator(), new FakeClock(Now));

        var result = await handler.HandleAsync(new global::Ago.Calendar.Application.UseCases.ErasePerson.ErasePerson(OperatorId, TenantId, PersonId), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("person_erase.not_found", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WhenTheStoreErases_ReturnsSuccess_AndStagesThePersonErasedEnvelope()
    {
        var permissions = new FakePermissionChecker();
        var store = new FakePersonEraseStore(erases: true);
        var handler = new ErasePersonHandler(
            new FakePersonRecordRepository(SeededPerson()), store, permissions, new FakeIdGenerator(), new FakeClock(Now));

        var result = await handler.HandleAsync(new global::Ago.Calendar.Application.UseCases.ErasePerson.ErasePerson(OperatorId, TenantId, PersonId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var attempt = Assert.Single(store.Attempts);
        Assert.Equal(TenantId, attempt.TenantId);
        Assert.Equal(PersonId, attempt.PersonId);
        Assert.Equal(Now, attempt.Now);
        Assert.Equal(nameof(Ago.Calendar.Contracts.PersonErased), attempt.PersonErasedEvent.Type);
        Assert.Equal(PersonId.ToString(), attempt.PersonErasedEvent.PartitionKey);
    }

    private static PersonRecord SeededPerson() => PersonRecord.Register(PersonId, TenantId, Phone(), Now);

    private static PhoneNumber Phone() => new("+79990001122");

    /// <summary>Records every attempt it was asked to make and answers with the outcome the test
    /// named - the identical "hand-written fake, not a mocking framework" shape testing.md and every
    /// other fake in this project already follows.</summary>
    private sealed class FakePersonEraseStore(bool erases) : IPersonEraseStore
    {
        public List<PersonEraseAttempt> Attempts { get; } = [];

        public Task<bool> TryEraseAsync(PersonEraseAttempt attempt, CancellationToken cancellationToken)
        {
            Attempts.Add(attempt);
            return Task.FromResult(erases);
        }
    }

    /// <summary>Answers with <paramref name="record"/> on the handler's own pre-transaction existence
    /// check, then <see langword="null"/> on every call after - simulating a concurrent second erasure
    /// that removed the row in the gap before the guarded store attempt ran.</summary>
    private sealed class OnceThenGoneFakePersonRecordRepository(PersonRecord record) : IPersonRecordRepository
    {
        private int _calls;

        public Task<PersonRecord?> GetByIdAsync(Guid personId, CancellationToken cancellationToken)
        {
            _calls++;
            return Task.FromResult(_calls == 1 && personId == record.PersonId ? record : null);
        }

        public Task<DateTimeOffset?> FindPhoneVerifiedAtAsync(
            TenantId tenantId, PhoneNumber phone, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not reached by ErasePersonHandler.");

        public Task AddAsync(PersonRecord record, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not reached by ErasePersonHandler.");

        public Task SaveAsync(PersonRecord record, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not reached by ErasePersonHandler.");
    }
}
