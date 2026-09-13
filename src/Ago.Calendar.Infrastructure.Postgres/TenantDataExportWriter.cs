using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;
using Ago.Platform.Kernel;
using Npgsql;

namespace Ago.Calendar.Infrastructure.Postgres;

/// <summary>
/// `22-31`: this product's own <see cref="ITenantDataExporter"/> - a `.zip` of `manifest.json` plus one
/// JSON-Lines file per table, chosen deliberately close to
/// <c>Ago.Chat.Worker.SiteExportArchiveWriter</c>'s own shape (same reasoning: one JSON object per line,
/// never a JSON array, so a reader can process a store without loading the whole file into memory) even
/// though nothing forces the two products to agree - `adr/0149` rule 3 means chat never opens this
/// archive at all, so this shape is chosen for this product's own future readers (a support engineer, a
/// later retention job), not for compatibility with the sibling format.
///
/// <para><b>No calendar object storage (this item's own Answered section, 2026-09-13) - and a genuinely
/// streamed HTTP response, found to need a local temp file after all.</b> The first shape this class
/// took wrote the <c>ZipArchive</c> straight onto <paramref name="destination"/>, on the theory that
/// .NET's <c>ZipArchive</c> in <c>Create</c> mode does not require a seekable stream (it falls back to a
/// trailing data descriptor instead of a pre-computed header). That theory broke against a real ASP.NET
/// Core response body: the data-descriptor fallback still issues one synchronous <c>Write</c> per entry
/// when the entry closes, and Kestrel's own response stream throws
/// <see cref="InvalidOperationException"/> on synchronous IO by default - found running
/// <c>TenantExportEndpointTests</c> against a real host, not reasoned out in advance. The fix is the
/// identical trade-off <c>Ago.Chat.Worker.SiteExportJob</c>'s own remarks already state and justify for
/// the sibling product's whole archive: build the zip onto a local, bounded temp file (seekable, so
/// <c>ZipArchive</c> never needs the data-descriptor fallback at all), then stream that file's bytes
/// onto <paramref name="destination"/> with a plain, fully-async <see cref="Stream.CopyToAsync(Stream, CancellationToken)"/>.
/// "No calendar object storage" is unaffected - a temp file this process creates and deletes within one
/// request is not a durable artifact anywhere, unlike the presigned-S3 shape this section explicitly
/// declined to build. Every per-table read below is still a forward-only <see cref="NpgsqlDataReader"/>
/// loop - one row in memory at a time - so the "whole tenant's calendar history in one call" the backlog
/// item's own Answered section accepts is a statement about one HTTP request/response, never about this
/// process holding the payload whole in memory.</para>
///
/// <para><b>A tenant with no row at all writes a valid, mostly-empty archive - not an error.</b> The
/// identical idempotent treatment <see cref="ITenantErasureRepository.EraseAsync"/> already gives the
/// same two cases (already erased, or never provisioned) on the destructive side:
/// <see cref="WriteAsync"/> still produces `manifest.json` and every table's own (empty) member, with
/// `tenantExisted: false` recorded so a reader can tell the two apart from the one fact this port does
/// know, without this port trying to guess which of "never provisioned" and "already erased" is
/// true.</para>
/// </summary>
public sealed class TenantDataExportWriter(NpgsqlDataSource dataSource, IClock clock) : ITenantDataExporter
{
    private const int ExportFormatVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public int FormatVersion => ExportFormatVersion;

    public string ContentType => "application/zip";

    public async Task WriteAsync(TenantId tenantId, Stream destination, CancellationToken cancellationToken)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"ago-calendar-export-{tenantId.Value:N}-{Guid.NewGuid():N}.zip");
        try
        {
            await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken))
            await using (var fileStream = new FileStream(
                tempPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, bufferSize: 81920, useAsync: true))
            {
                using (var archive = new ZipArchive(fileStream, ZipArchiveMode.Create, leaveOpen: true))
                {
                    var tenantExisted = await WriteTenantAsync(archive, connection, tenantId, cancellationToken);
                    await WriteCalendarsAsync(archive, connection, tenantId, cancellationToken);
                    await WriteWorkersAsync(archive, connection, tenantId, cancellationToken);
                    await WriteWorkingHoursRulesAsync(archive, connection, tenantId, cancellationToken);
                    await WriteServicesAsync(archive, connection, tenantId, cancellationToken);
                    await WriteCustomersAsync(archive, connection, tenantId, cancellationToken);
                    await WriteEventsAsync(archive, connection, tenantId, cancellationToken);

                    await WriteManifestAsync(archive, tenantId, tenantExisted, clock.UtcNow, cancellationToken);
                }

                // The archive is flushed and its central directory written the moment the `using`
                // block above disposes it (ZipArchive.Dispose() is what actually writes the file's
                // trailing structure) - only after that is the file's full, final byte sequence on
                // disk to stream back out.
                fileStream.Position = 0;
                await fileStream.CopyToAsync(destination, cancellationToken);
            }
        }
        finally
        {
            // Best-effort, the identical "must not mask the real exception this method may already be
            // unwinding with" tolerance Ago.Chat.Worker.SiteExportJob.ProcessExportAsync's own remarks
            // state for its own temp-file cleanup.
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch (IOException)
            {
                // Left on disk for the OS's own temp-directory hygiene to eventually reclaim - the
                // identical trade-off SiteExportJob's own remarks accept for the identical failure mode.
            }
        }
    }

    /// <summary>Written last, the identical reason
    /// <c>Ago.Chat.Worker.SiteExportArchiveWriter.WriteAsync</c>'s own remarks give for its own manifest
    /// move: nothing about a zip's own entries depends on write order, and <see cref="tenantExisted"/>
    /// is only known once <see cref="WriteTenantAsync"/> has already run.</summary>
    private static async Task WriteManifestAsync(
        ZipArchive archive, TenantId tenantId, bool tenantExisted, DateTimeOffset exportedAt, CancellationToken cancellationToken)
    {
        var manifest = new ManifestDocument(
            ExportFormatVersion,
            tenantId.Value,
            exportedAt,
            tenantExisted,
            Stores: ["tenant", "calendars", "workers", "workingHoursRules", "services", "customers", "events"]);

        var entry = archive.CreateEntry("manifest.json", CompressionLevel.Fastest);
        await using var entryStream = entry.Open();
        await JsonSerializer.SerializeAsync(entryStream, manifest, JsonOptions, cancellationToken);
    }

    private static async Task<bool> WriteTenantAsync(
        ZipArchive archive, NpgsqlConnection connection, TenantId tenantId, CancellationToken cancellationToken)
    {
        const string sql = """
            select id, name, public_key, allowed_origins, created_at, auto_provisioned, worker_quota
            from tenants
            where id = @tenantId
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("tenantId", tenantId.Value);

        TenantExportRow? row = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                row = new TenantExportRow(
                    reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
                    reader.GetFieldValue<string[]>(3), reader.GetFieldValue<DateTimeOffset>(4),
                    reader.GetBoolean(5), reader.GetInt32(6));
            }
        }

        var entry = archive.CreateEntry("tenant.json", CompressionLevel.Fastest);
        await using var entryStream = entry.Open();
        await JsonSerializer.SerializeAsync(entryStream, row, JsonOptions, cancellationToken);

        return row is not null;
    }

    private static async Task WriteCalendarsAsync(
        ZipArchive archive, NpgsqlConnection connection, TenantId tenantId, CancellationToken cancellationToken)
    {
        const string sql = """
            select id, name, time_zone, is_published, created_at
            from calendars
            where tenant_id = @tenantId
            order by id
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("tenantId", tenantId.Value);

        var entry = archive.CreateEntry("calendars.jsonl", CompressionLevel.Fastest);
        await using var entryStream = entry.Open();
        await using var writer = new StreamWriter(entryStream, Encoding.UTF8);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new CalendarExportRow(
                reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3),
                reader.GetFieldValue<DateTimeOffset>(4));
            await writer.WriteLineAsync(JsonSerializer.Serialize(row, JsonOptions));
        }
    }

    private static async Task WriteWorkersAsync(
        ZipArchive archive, NpgsqlConnection connection, TenantId tenantId, CancellationToken cancellationToken)
    {
        const string sql = """
            select id, last_name, first_name, middle_name, display_name, display_name_is_custom, is_active,
                   created_at, updated_at
            from workers
            where tenant_id = @tenantId
            order by id
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("tenantId", tenantId.Value);

        var entry = archive.CreateEntry("workers.jsonl", CompressionLevel.Fastest);
        await using var entryStream = entry.Open();
        await using var writer = new StreamWriter(entryStream, Encoding.UTF8);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new WorkerExportRow(
                reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4), reader.GetBoolean(5),
                reader.GetBoolean(6), reader.GetFieldValue<DateTimeOffset>(7), reader.GetFieldValue<DateTimeOffset>(8));
            await writer.WriteLineAsync(JsonSerializer.Serialize(row, JsonOptions));
        }
    }

    /// <summary>`22-31`'s own "schedule" half of the item's Contents list - the recurring rule a worker's
    /// slots are materialised from. <c>worker_schedules</c> (slot length/buffer/horizon/cursor) is
    /// deliberately not exported alongside it: it is this product's own materialisation bookkeeping, not
    /// a fact about the tenant's business a subject access request is normally understood to cover - the
    /// identical "operational metadata, not tenant content" line
    /// <c>Ago.Chat.Worker.SiteExportArchiveWriter</c>'s own remarks already draw for excluding
    /// <c>webhook_deliveries</c>/`channel_credentials`.</summary>
    private static async Task WriteWorkingHoursRulesAsync(
        ZipArchive archive, NpgsqlConnection connection, TenantId tenantId, CancellationToken cancellationToken)
    {
        const string sql = """
            select r.id, r.worker_id, r.calendar_id, r.day_of_week, r.starts_at, r.ends_at
            from working_hours_rules r
            join workers w on w.id = r.worker_id
            where w.tenant_id = @tenantId
            order by r.worker_id, r.calendar_id, r.id
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("tenantId", tenantId.Value);

        var entry = archive.CreateEntry("working_hours_rules.jsonl", CompressionLevel.Fastest);
        await using var entryStream = entry.Open();
        await using var writer = new StreamWriter(entryStream, Encoding.UTF8);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new WorkingHoursRuleExportRow(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetString(3),
                reader.GetFieldValue<TimeOnly>(4), reader.GetFieldValue<TimeOnly>(5));
            await writer.WriteLineAsync(JsonSerializer.Serialize(row, JsonOptions));
        }
    }

    private static async Task WriteServicesAsync(
        ZipArchive archive, NpgsqlConnection connection, TenantId tenantId, CancellationToken cancellationToken)
    {
        const string sql = """
            select id, name, duration_minutes, description, price_minor_units, price_currency_code, price_is_from
            from services
            where tenant_id = @tenantId
            order by id
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("tenantId", tenantId.Value);

        var entry = archive.CreateEntry("services.jsonl", CompressionLevel.Fastest);
        await using var entryStream = entry.Open();
        await using var writer = new StreamWriter(entryStream, Encoding.UTF8);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new ServiceExportRow(
                reader.GetGuid(0), reader.GetString(1), reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetBoolean(6));
            await writer.WriteLineAsync(JsonSerializer.Serialize(row, JsonOptions));
        }
    }

    /// <summary>The one entity `personal-data.md` names as the sole natural-person data this product
    /// holds (<see cref="Customer"/>'s own remarks) - phone, display name and free-text notes travel
    /// unmasked, unlike <c>ContactsReadStore</c>'s own console-facing read, because a tenant exporting
    /// their own data is not the "an operator without <c>customer:read</c>" case that store's masking
    /// exists for.</summary>
    private static async Task WriteCustomersAsync(
        ZipArchive archive, NpgsqlConnection connection, TenantId tenantId, CancellationToken cancellationToken)
    {
        const string sql = """
            select id, phone, source, source_contact_id, display_name, notes, phone_verified_at,
                   operator_confirmed_phone_at, no_show_count, first_seen_at, last_seen_at,
                   merged_into_customer_id, merged_at
            from customers
            where tenant_id = @tenantId
            order by id
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("tenantId", tenantId.Value);

        var entry = archive.CreateEntry("customers.jsonl", CompressionLevel.Fastest);
        await using var entryStream = entry.Open();
        await using var writer = new StreamWriter(entryStream, Encoding.UTF8);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new CustomerExportRow(
                reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetGuid(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
                reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7),
                reader.GetInt32(8),
                reader.GetFieldValue<DateTimeOffset>(9),
                reader.GetFieldValue<DateTimeOffset>(10),
                reader.IsDBNull(11) ? null : reader.GetGuid(11),
                reader.IsDBNull(12) ? null : reader.GetFieldValue<DateTimeOffset>(12));
            await writer.WriteLineAsync(JsonSerializer.Serialize(row, JsonOptions));
        }
    }

    private static async Task WriteEventsAsync(
        ZipArchive archive, NpgsqlConnection connection, TenantId tenantId, CancellationToken cancellationToken)
    {
        const string sql = """
            select id, calendar_id, worker_id, service_id, customer_id, booking_id, starts_at, ends_at,
                   local_date, status, confirmation_deadline, created_at
            from events
            where tenant_id = @tenantId
            order by starts_at, id
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("tenantId", tenantId.Value);

        var entry = archive.CreateEntry("events.jsonl", CompressionLevel.Fastest);
        await using var entryStream = entry.Open();
        await using var writer = new StreamWriter(entryStream, Encoding.UTF8);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new EventExportRow(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2),
                reader.IsDBNull(3) ? null : reader.GetGuid(3),
                reader.IsDBNull(4) ? null : reader.GetGuid(4),
                reader.IsDBNull(5) ? null : reader.GetGuid(5),
                reader.GetFieldValue<DateTimeOffset>(6), reader.GetFieldValue<DateTimeOffset>(7),
                reader.GetFieldValue<DateOnly>(8), reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetFieldValue<DateTimeOffset>(10),
                reader.GetFieldValue<DateTimeOffset>(11));
            await writer.WriteLineAsync(JsonSerializer.Serialize(row, JsonOptions));
        }
    }

    private sealed record ManifestDocument(
        int FormatVersion, Guid TenantId, DateTimeOffset ExportedAt, bool TenantExisted, IReadOnlyList<string> Stores);

    private sealed record TenantExportRow(
        Guid Id, string Name, string PublicKey, IReadOnlyList<string> AllowedOrigins, DateTimeOffset CreatedAt,
        bool AutoProvisioned, int WorkerQuota);

    private sealed record CalendarExportRow(Guid Id, string Name, string TimeZone, bool IsPublished, DateTimeOffset CreatedAt);

    private sealed record WorkerExportRow(
        Guid Id, string LastName, string FirstName, string? MiddleName, string DisplayName, bool DisplayNameIsCustom,
        bool IsActive, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

    private sealed record WorkingHoursRuleExportRow(
        Guid Id, Guid WorkerId, Guid CalendarId, string DayOfWeek, TimeOnly StartsAt, TimeOnly EndsAt);

    private sealed record ServiceExportRow(
        Guid Id, string Name, int DurationMinutes, string? Description, long? PriceMinorUnits,
        string? PriceCurrencyCode, bool PriceIsFrom);

    private sealed record CustomerExportRow(
        Guid Id, string Phone, string Source, Guid? SourceContactId, string? DisplayName, string? Notes,
        DateTimeOffset? PhoneVerifiedAt, DateTimeOffset? PhoneConfirmedByOperatorAt, int NoShowCount,
        DateTimeOffset FirstSeenAt, DateTimeOffset LastSeenAt, Guid? MergedIntoCustomerId, DateTimeOffset? MergedAt);

    private sealed record EventExportRow(
        Guid Id, Guid CalendarId, Guid WorkerId, Guid? ServiceId, Guid? CustomerId, Guid? BookingId,
        DateTimeOffset StartsAt, DateTimeOffset EndsAt, DateOnly LocalDate, string Status,
        DateTimeOffset? ConfirmationDeadline, DateTimeOffset CreatedAt);
}
