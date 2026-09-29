using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Application.UseCases.PersonRecognition;
using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.Tests;

/// <summary>`26-268`§2a/`adr/0188`: the phone-based recognition read, every port faked - the permission
/// gate, the phone-shape rejection, and the rung-masking decision are the whole of what this handler
/// adds over the read store, so that is the whole of what these tests are about. Several-matches-for-one-
/// number, tenant isolation, and "surfacing never merges" against a real Postgres live in
/// `PersonRecognitionReadStoreTests` (`Ago.Calendar.Integration.Tests`).</summary>
public class GetPersonCandidatesByPhoneHandlerTests
{
    private static readonly TenantId TenantId = new(new Guid("11111111-1111-1111-1111-111111111111"));
    private static readonly OperatorId Caller = new(new Guid("22222222-2222-2222-2222-222222222222"));
    private const string Phone = "+79990000001";

    [Fact]
    public async Task WithCustomerRead_ReturnsTheStoresRows()
    {
        var row = ARow();
        var store = new FakePersonRecognitionReadStore(row);
        var handler = new GetPersonCandidatesByPhoneHandler(
            store, Permissive(), new FakeContactVisibilityProjectionStore());

        var result = await handler.HandleAsync(
            new GetPersonCandidatesByPhone(Caller, TenantId, Phone), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(row.PersonId, Assert.Single(result.Value).PersonId);
        var asked = Assert.Single(store.AskedFor);
        Assert.Equal(TenantId, asked.TenantId);
        Assert.Equal(Phone, asked.Phone);
    }

    [Fact]
    public async Task WithNoMatch_ReturnsAnEmptyList_NeverAnError()
    {
        // `docs/backlog/26-268-manual-booking-entry.md` §3.4: "no match -> proceed to new-client entry" -
        // a real, honest empty state, never a refusal.
        var store = new FakePersonRecognitionReadStore();
        var handler = new GetPersonCandidatesByPhoneHandler(
            store, Permissive(), new FakeContactVisibilityProjectionStore());

        var result = await handler.HandleAsync(
            new GetPersonCandidatesByPhone(Caller, TenantId, Phone), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Empty(result.Value);
    }

    [Fact]
    public async Task SeveralMatches_AreAllReturned_NeverCollapsedIntoOne()
    {
        // `adr/0147`/`adr/0188`: surfacing, never merging - two distinct people sharing a number come
        // back as two distinct candidates, not one row a caller could mistake for a single resolved
        // identity.
        var first = ARow(personId: Guid.NewGuid());
        var second = ARow(personId: Guid.NewGuid());
        var store = new FakePersonRecognitionReadStore(first, second);
        var handler = new GetPersonCandidatesByPhoneHandler(
            store, Permissive(), new FakeContactVisibilityProjectionStore());

        var result = await handler.HandleAsync(
            new GetPersonCandidatesByPhone(Caller, TenantId, Phone), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(2, result.Value.Count);
        Assert.Equal(
            new[] { first.PersonId, second.PersonId }.OrderBy(id => id),
            result.Value.Select(row => row.PersonId).OrderBy(id => id));
    }

    [Fact]
    public async Task WithoutCustomerRead_IsRefused_AndNeverAsksTheStore()
    {
        var store = new FakePersonRecognitionReadStore();
        var permissions = new FakePermissionChecker();
        permissions.Deny(Permission.CustomerRead);
        var handler = new GetPersonCandidatesByPhoneHandler(
            store, permissions, new FakeContactVisibilityProjectionStore());

        var result = await handler.HandleAsync(
            new GetPersonCandidatesByPhone(Caller, TenantId, Phone), CancellationToken.None);

        Assert.Equal("person_recognition.forbidden", result.Error!.Value.Code);
        Assert.Empty(store.AskedFor);
    }

    [Fact]
    public async Task AMalformedPhone_IsRejected_AndNeverReachesTheStore()
    {
        var store = new FakePersonRecognitionReadStore();
        var handler = new GetPersonCandidatesByPhoneHandler(
            store, Permissive(), new FakeContactVisibilityProjectionStore());

        var result = await handler.HandleAsync(
            new GetPersonCandidatesByPhone(Caller, TenantId, "12345"), CancellationToken.None);

        Assert.Equal("person_recognition.invalid_phone", result.Error!.Value.Code);
        Assert.Empty(store.AskedFor);
    }

    [Fact]
    public async Task OnTheMaskedRung_AsksTheStoreToMask()
    {
        var store = new FakePersonRecognitionReadStore(ARow());
        var visibility = new FakeContactVisibilityProjectionStore(ContactVisibility.MaskedWithReveal);
        var handler = new GetPersonCandidatesByPhoneHandler(store, Permissive(), visibility);

        var result = await handler.HandleAsync(
            new GetPersonCandidatesByPhone(Caller, TenantId, Phone), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.True(Assert.Single(store.AskedFor).Mask);
    }

    [Fact]
    public async Task OnTheVisibleRung_NeverAsksTheStoreToMask()
    {
        var store = new FakePersonRecognitionReadStore();
        var visibility = new FakeContactVisibilityProjectionStore(ContactVisibility.Visible);
        var handler = new GetPersonCandidatesByPhoneHandler(store, Permissive(), visibility);

        var result = await handler.HandleAsync(
            new GetPersonCandidatesByPhone(Caller, TenantId, Phone), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.False(Assert.Single(store.AskedFor).Mask);
    }

    private static PersonRecognitionCandidateRow ARow(Guid? personId = null) => new(
        personId ?? Guid.NewGuid(),
        Phone,
        false,
        0,
        3,
        null,
        null,
        new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 2, 1, 9, 0, 0, TimeSpan.Zero));

    private static FakePermissionChecker Permissive() => new();
}
