using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Calendar.Infrastructure.Postgres.Migrations;

/// <summary>
/// `20-14`: creates <c>worker_schedules</c>, backfills one row per worker the old buffer actually
/// governed, then drops <c>calendars.buffer_minutes</c> - the one migration this item owns, the
/// same "real, tested, from-empty-database backfill" shape `20-13`'s own
/// <c>Stage20AddWorkerNameFieldsAndTimestamps</c> established.
///
/// <para><b>Order matters, and it is why this file is not the raw scaffold.</b> <c>dotnet ef
/// migrations add</c> produced the drop-then-create order the model diff implies; this migration
/// creates the table and copies the value across <i>before</i> the column that value came from is
/// gone; the scaffolded drop moves to the end.</para>
///
/// <para><b>Backfilled for a worker only when the old code path actually materialised him
/// something</b> - <c>MaterializeAvailabilityHandler</c>, before this item, skipped a worker with
/// no <c>WorkingHoursRule</c> row and skipped one with no offered service (there was no length to
/// derive a slot from). A worker satisfying neither condition never had a bookable slot to begin
/// with, and writing him a schedule here would be inventing a fact rather than preserving one -
/// the same "honest instead of clever" principle `20-13`'s own migration doc comment states, ap
/// plied to a different column.</para>
///
/// <para><b>The four numbers a backfilled schedule is seeded with, and where each one comes
/// from.</b> <c>kind</c> is always <c>'Weekly'</c> - a worker migrating from `20-02`'s model had
/// weekday rules and nothing else, since the cycle kind did not exist before this item.
/// <c>slot_minutes</c> is <c>MAX(service.duration_minutes)</c> over the worker's own offered
/// services - <c>MaterializeAvailabilityHandler.SlotLengthFor</c>'s exact rule, reproduced once in
/// SQL so the very next materialisation run after this migration produces the identical grid the
/// one before it did. <c>buffer_minutes</c> is the worker's calendar's own current value - the
/// item's own "Decided" section, verbatim. <c>horizon_days</c> is
/// <c>WorkerSchedule.DefaultHorizonDays</c> (30) - the number that used to live on
/// <c>AvailabilityMaterializationJobOptions.HorizonDays</c> and governed every worker identically;
/// there is no per-worker history to recover it from more precisely than that, so the honest
/// choice is the same default a brand new schedule gets. <c>materialize_from</c> is
/// <c>CURRENT_DATE</c> at the instant this migration runs - not a fabricated backdate, and safe
/// precisely because the materialiser's own non-destructive rule (a day with any event row is
/// never regenerated) means re-scanning a window that was already partly cut inserts nothing for
/// the days that were.</para>
/// </summary>
public partial class Stage20AddWorkerScheduleAndRemoveCalendarBuffer : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "worker_schedules",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                worker_id = table.Column<Guid>(type: "uuid", nullable: false),
                kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                cycle_anchor = table.Column<DateOnly>(type: "date", nullable: true),
                cycle_working_days = table.Column<int>(type: "integer", nullable: true),
                cycle_rest_days = table.Column<int>(type: "integer", nullable: true),
                cycle_starts_at = table.Column<TimeOnly>(type: "time", nullable: true),
                cycle_ends_at = table.Column<TimeOnly>(type: "time", nullable: true),
                slot_minutes = table.Column<int>(type: "integer", nullable: false),
                buffer_minutes = table.Column<int>(type: "integer", nullable: false),
                horizon_days = table.Column<int>(type: "integer", nullable: false),
                materialize_from = table.Column<DateOnly>(type: "date", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_worker_schedules", x => x.id);
                table.ForeignKey(
                    name: "FK_worker_schedules_workers_worker_id",
                    column: x => x.worker_id,
                    principalTable: "workers",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "ux_worker_schedules_worker_id",
            table: "worker_schedules",
            column: "worker_id",
            unique: true);

        // gen_random_uuid() rather than a UUIDv7 helper: nothing downstream decodes this row's own
        // id for a timestamp the way `20-13`'s migration decoded a worker's - created_at/updated_at
        // are real columns on this table already, so the id carries no second meaning to preserve.
        migrationBuilder.Sql(
            """
            INSERT INTO worker_schedules
                (id, worker_id, kind, slot_minutes, buffer_minutes, horizon_days, materialize_from, created_at, updated_at)
            SELECT
                gen_random_uuid(),
                w.id,
                'Weekly',
                (SELECT MAX(s.duration_minutes)
                   FROM worker_services ws
                   JOIN services s ON s.id = ws.service_id
                  WHERE ws.worker_id = w.id),
                c.buffer_minutes,
                30,
                CURRENT_DATE,
                now(),
                now()
            FROM workers w
            JOIN calendar_workers cw ON cw.worker_id = w.id
            JOIN calendars c ON c.id = cw.calendar_id
            WHERE EXISTS (SELECT 1 FROM working_hours_rules r WHERE r.worker_id = w.id)
              AND EXISTS (SELECT 1 FROM worker_services ws WHERE ws.worker_id = w.id)
            """);

        migrationBuilder.DropColumn(
            name: "buffer_minutes",
            table: "calendars");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "buffer_minutes",
            table: "calendars",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.DropTable(
            name: "worker_schedules");
    }
}
