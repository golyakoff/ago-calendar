using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Ago.Calendar.Application.Abstractions;
using Dapper;
using static Ago.Calendar.Integration.Tests.CalendarApiFactory;

namespace Ago.Calendar.Integration.Tests;

/// <summary>
/// `22-31`: this product's own half of the item's own first Done-when - "an export of a tenant with the
/// calendar add-on contains the calendar's half... proven by opening the archive." Every assertion here
/// opens the real, returned bytes as a real <see cref="ZipArchive"/> and reads real rows back out of
/// them, the identical "prove the archive's actual contents, not merely that a 200 came back" bar
/// <c>Ago.Chat.Integration.Tests.SiteExportIntegrationTests</c>'s own class remarks already set for the
/// sibling product's export.
///
/// <para><b>Authenticated the way `22-11`'s registration calls already are</b> - the provisioning
/// secret, the identical reason <see cref="TenantErasureEndpointTests"/>'s own remarks give for its
/// sibling route on the same path.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class TenantExportEndpointTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string ProvisioningSecretHeaderName = "X-Ago-Module-Provisioning-Secret";
    private const string ExportFormatVersionHeaderName = "X-Ago-Export-Format-Version";

    private CalendarApiFactory _factory = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _factory = new CalendarApiFactory(fixture);
        _client = _factory.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task Export_ForATenantWithEverything_ReturnsARealArchive_ContainingEveryTable()
    {
        var seed = await CalendarSeed.WriteAsync(fixture, publicKey: $"export-{CalendarSeed.NewId():N}"[..24], workerQuota: 1);
        await CalendarSeed.AddWorkingHoursAsync(
            fixture, seed, new TimeOnly(9, 0), new TimeOnly(18, 0), DayOfWeek.Monday, DayOfWeek.Tuesday);

        var eventId = Guid.NewGuid();
        await using (var connection = await fixture.DataSource.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                """
                insert into events
                    (id, tenant_id, calendar_id, worker_id, service_id, person_id, status,
                     starts_at, ends_at, local_date, created_at)
                values
                    (@id, @tenantId, @calendarId, @workerId, @serviceId, @personId, 'Confirmed',
                     @startsAt, @endsAt, (@startsAt at time zone 'utc')::date, @now)
                """,
                new
                {
                    id = eventId,
                    tenantId = seed.Tenant.Id.Value,
                    calendarId = seed.Calendar.Id.Value,
                    workerId = seed.Worker.Id.Value,
                    serviceId = seed.Service.Id.Value,
                    personId = seed.Person.PersonId,
                    startsAt = CalendarSeed.Now,
                    endsAt = CalendarSeed.Now.AddMinutes(45),
                    now = CalendarSeed.Now,
                });
        }

        using var response = await ExportAsync(seed.Tenant.Id.Value);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("1", response.Headers.GetValues(ExportFormatVersionHeaderName).Single());

        using var archive = new ZipArchive(await response.Content.ReadAsStreamAsync(), ZipArchiveMode.Read);

        var manifest = await ReadJsonAsync(archive, "manifest.json");
        Assert.Equal(1, manifest.GetProperty("formatVersion").GetInt32());
        Assert.Equal(seed.Tenant.Id.Value, manifest.GetProperty("tenantId").GetGuid());
        Assert.True(manifest.GetProperty("tenantExisted").GetBoolean());

        var tenant = await ReadJsonAsync(archive, "tenant.json");
        Assert.Equal(seed.Tenant.Id.Value, tenant.GetProperty("id").GetGuid());
        Assert.Equal("Barbershop", tenant.GetProperty("name").GetString());

        var calendars = await ReadJsonLinesAsync(archive, "calendars.jsonl");
        Assert.Equal(seed.Calendar.Id.Value, Assert.Single(calendars).GetProperty("id").GetGuid());

        var workers = await ReadJsonLinesAsync(archive, "workers.jsonl");
        var workerRow = Assert.Single(workers);
        Assert.Equal(seed.Worker.Id.Value, workerRow.GetProperty("id").GetGuid());
        Assert.Equal("Alex Doe", workerRow.GetProperty("displayName").GetString());

        var rules = await ReadJsonLinesAsync(archive, "working_hours_rules.jsonl");
        Assert.Equal(2, rules.Count);

        var services = await ReadJsonLinesAsync(archive, "services.jsonl");
        Assert.Equal(seed.Service.Id.Value, Assert.Single(services).GetProperty("id").GetGuid());

        var persons = await ReadJsonLinesAsync(archive, "person_records.jsonl");
        var personRow = Assert.Single(persons);
        Assert.Equal(seed.Person.PersonId, personRow.GetProperty("personId").GetGuid());
        Assert.Equal("+79991234567", personRow.GetProperty("phone").GetString());

        var events = await ReadJsonLinesAsync(archive, "events.jsonl");
        Assert.Equal(eventId, Assert.Single(events).GetProperty("id").GetGuid());
    }

    /// <summary>The item's own third Done-when, at this product's own boundary: a tenant this deployment
    /// never heard of still gets a valid, well-formed archive back - <see cref="ITenantDataExporter"/>'s
    /// own idempotent "not found is not a failure" answer, the same one erasure already gives -
    /// distinguished from a real tenant only by <c>tenantExisted: false</c> and every table's own member
    /// being empty.</summary>
    [Fact]
    public async Task Export_ForATenantThatNeverExisted_ReturnsAValidButEmptyArchive()
    {
        using var response = await ExportAsync(Guid.NewGuid());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var archive = new ZipArchive(await response.Content.ReadAsStreamAsync(), ZipArchiveMode.Read);

        var manifest = await ReadJsonAsync(archive, "manifest.json");
        Assert.False(manifest.GetProperty("tenantExisted").GetBoolean());

        var tenant = await ReadJsonAsync(archive, "tenant.json");
        Assert.Equal(JsonValueKind.Null, tenant.ValueKind);

        Assert.Empty(await ReadJsonLinesAsync(archive, "person_records.jsonl"));
        Assert.Empty(await ReadJsonLinesAsync(archive, "events.jsonl"));
    }

    [Fact]
    public async Task Export_WithAWrongProvisioningSecret_IsRefused()
    {
        var seed = await CalendarSeed.WriteAsync(fixture, publicKey: $"export2-{CalendarSeed.NewId():N}"[..24]);

        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"/api/v1/module-registrations/{seed.Tenant.Id.Value}/tenant-data");
        request.Headers.Add(ProvisioningSecretHeaderName, "not-the-configured-provisioning-secret");

        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Export_WithNoProvisioningSecretHeaderAtAll_IsRefused()
    {
        var seed = await CalendarSeed.WriteAsync(fixture, publicKey: $"export3-{CalendarSeed.NewId():N}"[..24]);

        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"/api/v1/module-registrations/{seed.Tenant.Id.Value}/tenant-data");

        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ------------------------------------------------------------------------------------------

    private async Task<HttpResponseMessage> ExportAsync(Guid tenantId)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"/api/v1/module-registrations/{tenantId}/tenant-data");
        request.Headers.Add(ProvisioningSecretHeaderName, TestProvisioningSecret);
        return await _client.SendAsync(request);
    }

    private static async Task<JsonElement> ReadJsonAsync(ZipArchive archive, string entryName)
    {
        var entry = archive.GetEntry(entryName) ?? throw new InvalidOperationException($"Archive has no entry '{entryName}'.");
        await using var stream = entry.Open();
        using var document = await JsonDocument.ParseAsync(stream);
        return document.RootElement.Clone();
    }

    private static async Task<IReadOnlyList<JsonElement>> ReadJsonLinesAsync(ZipArchive archive, string entryName)
    {
        var entry = archive.GetEntry(entryName) ?? throw new InvalidOperationException($"Archive has no entry '{entryName}'.");
        await using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        var lines = new List<JsonElement>();
        string? line;
        while ((line = await reader.ReadLineAsync()) is not null)
        {
            if (line.Length == 0)
            {
                continue;
            }

            lines.Add(JsonDocument.Parse(line).RootElement.Clone());
        }

        return lines;
    }
}
