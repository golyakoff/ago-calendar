using System.Net;
using System.Net.Http.Json;
using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Application.UseCases.PersonRecognition;
using Ago.Calendar.Contracts;
using Ago.Calendar.Domain;
using Ago.Calendar.Infrastructure.Postgres;
using Microsoft.EntityFrameworkCore;

namespace Ago.Calendar.Integration.Tests;

/// <summary>
/// `26-268`§2a/`adr/0188`: the phone-based recognition read, against a real Postgres - the same shape
/// `ContactsReportTests`/`PersonBookingsTests` already establish for their own sibling reads (a Dapper
/// read store, tenant-isolated, gated by a permission the handler checks once). The one property that
/// matters most for this particular read - <see cref="SeveralPeopleSharingOneNumber_AreAllReturned_NeverMergedIntoOne"/> -
/// is `adr/0147`'s own "a phone is a hint, not proof" restated as a database-backed test: this store must
/// never collapse two people into one row just because they share a number.
///
/// <para>Phone numbers here are invented <c>+7999...</c> values belonging to nobody
/// (<c>personal-data.md</c>).</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public class PersonRecognitionReadStoreTests(PostgresFixture fixture)
{
    [Fact]
    public async Task OneMatch_ReturnsItWithItsBookingCount()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        await ABookedBookingAsync(seed);

        var store = new PersonRecognitionReadStore(fixture.DataSource);
        var rows = await store.FindByPhoneAsync(seed.Tenant.Id, seed.Person.Phone, mask: false, CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.Equal(seed.Person.PersonId, row.PersonId);
        Assert.Equal(seed.Person.Phone.Value, row.Phone);
        Assert.Equal(1, row.BookingCount);
    }

    [Fact]
    public async Task NoMatch_ReturnsAnEmptyList_NeverAnError()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);

        var store = new PersonRecognitionReadStore(fixture.DataSource);
        var rows = await store.FindByPhoneAsync(
            seed.Tenant.Id, new PhoneNumber("+79990009999"), mask: false, CancellationToken.None);

        Assert.Empty(rows);
    }

    [Fact]
    public async Task SeveralPeopleSharingOneNumber_AreAllReturned_NeverMergedIntoOne()
    {
        // `adr/0147`/`adr/0188`'s own central property: two people, one number, two rows - this read
        // surfaces both for the operator to tell apart, it never picks or merges.
        var seed = await CalendarSeed.WriteAsync(fixture);
        var second = PersonRecord.Register(CalendarSeed.NewId(), seed.Tenant.Id, seed.Person.Phone, CalendarSeed.Now);

        await using (var db = fixture.CreateDbContext())
        {
            db.PersonRecords.Add(second);
            await db.SaveChangesAsync();
        }

        var store = new PersonRecognitionReadStore(fixture.DataSource);
        var rows = await store.FindByPhoneAsync(seed.Tenant.Id, seed.Person.Phone, mask: false, CancellationToken.None);

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.PersonId == seed.Person.PersonId);
        Assert.Contains(rows, r => r.PersonId == second.PersonId);
    }

    [Fact]
    public async Task TheReadStore_IsTenantIsolated()
    {
        // Deliberately the *same* phone number in two different tenants - a real scenario, since
        // `person_records` carries no cross-tenant uniqueness on phone at all - so this proves the query
        // is gated on `tenant_id`, not on the number alone.
        var mine = await CalendarSeed.WriteAsync(fixture);
        var theirs = await CalendarSeed.WriteAsync(fixture);
        var theirPerson = PersonRecord.Register(CalendarSeed.NewId(), theirs.Tenant.Id, mine.Person.Phone, CalendarSeed.Now);

        await using (var db = fixture.CreateDbContext())
        {
            db.PersonRecords.Add(theirPerson);
            await db.SaveChangesAsync();
        }

        var store = new PersonRecognitionReadStore(fixture.DataSource);
        var rows = await store.FindByPhoneAsync(mine.Tenant.Id, mine.Person.Phone, mask: false, CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.Equal(mine.Person.PersonId, row.PersonId);
    }

    [Fact]
    public async Task ACallerHoldingCustomerRead_SeesTheMaskedPhone_WhenTheTenantsRungCallsForIt()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);

        await using (var db = fixture.CreateDbContext())
        {
            var visibility = new ContactVisibilityProjectionStore(db);
            await visibility.StageAsync(seed.Tenant.Id, ContactVisibility.MaskedWithReveal, CalendarSeed.Now, CancellationToken.None);
            await db.SaveChangesAsync();
        }

        var rows = await HandleAsync(seed.OperatorId, seed.Tenant.Id, seed.Person.Phone.Value);

        var row = Assert.Single(rows);
        Assert.True(row.Masked);
        Assert.NotEqual(seed.Person.Phone.Value, row.Phone);
    }

    [Fact]
    public async Task TheHandler_WithoutCustomerRead_IsRefused()
    {
        var seed = await CalendarSeed.WriteAsync(fixture);
        var strangerSubject = $"kc-{CalendarSeed.NewId():N}";
        var strangerId = OperatorId.FromExternalSubjectId(strangerSubject);
        string[] strangerPermissions = [Permission.BookingReject.Value, Permission.BookingCancel.Value];

        await using (var db = fixture.CreateDbContext())
        {
            var projections = new RoleAssignmentProjectionStore(db);
            await projections.StageAsync(
                strangerId, seed.Tenant.Id, strangerSubject, strangerPermissions, CalendarSeed.Now, CancellationToken.None);
            await db.SaveChangesAsync();
        }

        await using var reader = fixture.CreateDbContext();
        var result = await new GetPersonCandidatesByPhoneHandler(
                new PersonRecognitionReadStore(fixture.DataSource),
                new PermissionChecker(new RoleAssignmentProjectionStore(reader)),
                new ContactVisibilityProjectionStore(reader))
            .HandleAsync(
                new GetPersonCandidatesByPhone(strangerId, seed.Tenant.Id, seed.Person.Phone.Value),
                CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("person_recognition.forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task TheConsoleEndpoint_ReturnsTheCandidates_OverRealHttp()
    {
        await using var apiFactory = new ConsoleApiFactory(fixture);
        using var client = apiFactory.CreateClient();
        var seed = await CalendarSeed.WriteAsync(fixture);
        await ABookedBookingAsync(seed);

        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"/api/v1/console/contacts/by-phone?phone={Uri.EscapeDataString(seed.Person.Phone.Value)}");
        request.Headers.Add(ConsoleApiFactory.SubjectHeader, seed.ExternalSubjectId);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var candidates = await response.Content.ReadFromJsonAsync<PersonRecognitionCandidateResponse[]>();
        var candidate = Assert.Single(candidates!);
        Assert.Equal(seed.Person.PersonId, candidate.PersonId);
        Assert.Equal(1, candidate.BookingCount);
    }

    [Fact]
    public async Task TheConsoleEndpoint_ReturnsAnEmptyArray_ForANumberWithNoMatch_OverRealHttp()
    {
        await using var apiFactory = new ConsoleApiFactory(fixture);
        using var client = apiFactory.CreateClient();
        var seed = await CalendarSeed.WriteAsync(fixture);

        using var request = new HttpRequestMessage(
            HttpMethod.Get, "/api/v1/console/contacts/by-phone?phone=%2B79990009999");
        request.Headers.Add(ConsoleApiFactory.SubjectHeader, seed.ExternalSubjectId);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var candidates = await response.Content.ReadFromJsonAsync<PersonRecognitionCandidateResponse[]>();
        Assert.Empty(candidates!);
    }

    private async Task ABookedBookingAsync(SeededTenant seed)
    {
        var slot = CalendarSeed.Slot(seed, CalendarSeed.Now.AddHours(1));

        await using (var db = fixture.CreateDbContext())
        {
            db.Events.Add(slot);
            await db.SaveChangesAsync();
        }

        await using (var db = fixture.CreateDbContext())
        {
            var row = await db.Events.SingleAsync(e => e.Id == slot.Id);
            row.Claim(seed.Person.PersonId, seed.Service.Id, CalendarSeed.Now, CalendarSeed.Now.AddMinutes(1), slot.Id);
            row.Confirm(CalendarSeed.Now.AddMinutes(2));
            row.ClearDomainEvents();
            await db.SaveChangesAsync();
        }
    }

    private async Task<IReadOnlyList<PersonRecognitionCandidateRow>> HandleAsync(
        OperatorId operatorId, TenantId tenantId, string phone)
    {
        await using var db = fixture.CreateDbContext();
        var result = await new GetPersonCandidatesByPhoneHandler(
                new PersonRecognitionReadStore(fixture.DataSource),
                new PermissionChecker(new RoleAssignmentProjectionStore(db)),
                new ContactVisibilityProjectionStore(db))
            .HandleAsync(new GetPersonCandidatesByPhone(operatorId, tenantId, phone), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        return result.Value;
    }
}
