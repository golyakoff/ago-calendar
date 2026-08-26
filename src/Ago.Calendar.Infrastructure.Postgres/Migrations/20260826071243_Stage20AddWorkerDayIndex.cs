using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Calendar.Infrastructure.Postgres.Migrations;

/// <inheritdoc />
public partial class Stage20AddWorkerDayIndex : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // `20-02`. The index every question this item asks is answered from: "which of this
        // worker's days already have rows" (the materialiser's non-destructive rule), "what is
        // on this day" (both manual edits), and the day-scoped delete behind a day off. Two
        // equalities then a range, in that column order, which is the only order a B-tree can
        // serve all three from.
        //
        // Deliberately not partial, unlike ix_events_available and
        // ix_events_pending_confirmation. The rule turns on whether a day holds *any* row -
        // booked, blocked and cancelled included - so a status filter would hide exactly the
        // rows whose presence is the decision.
        //
        // This is the index adr/0049 stored `local_date` for instead of deriving it: an
        // `AT TIME ZONE` predicate is non-sargable, so no index could serve a per-day query and
        // every day-scoped edit would scan the worker's whole history.
        //
        // Its value is unmeasured, as data-model.md requires such a claim to be stated: the
        // integration test proves the planner *can* use it, not that it is faster than a scan on
        // a table this small.
        migrationBuilder.CreateIndex(
            name: "ix_events_worker_day",
            table: "events",
            columns: new[] { "calendar_id", "worker_id", "local_date" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "ix_events_worker_day",
            table: "events");
    }
}
