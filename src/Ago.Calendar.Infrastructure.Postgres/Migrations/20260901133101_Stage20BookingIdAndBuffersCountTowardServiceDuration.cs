using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Calendar.Infrastructure.Postgres.Migrations;

/// <summary>
/// `20-18`: the item's own entire migration budget, spent in one shot rather than two, because
/// `20-16` (landing the same wave) was told it needs none. Two independent additions, both real
/// schema and both backfilled honestly rather than left to default their way into a lie about the
/// data that predates them.
///
/// <para><b><c>events.booking_id</c></b> - the data-model decision the backlog item asked to be
/// confirmed or overturned when implementing: a grouping column on <c>events</c>, not a second
/// <c>bookings</c> table, because a booking carries no state its member rows do not already carry
/// identically. Nullable (an <see cref="EventStatus.Available"/> or
/// <see cref="EventStatus.Blocked"/> row has no booking), self-referencing (it names another row
/// of this same table - the run's own anchor, which is its own <c>id</c> when the booking is one
/// slot), and backfilled below for every row a claim already touched, so a row from before this
/// item existed groups correctly rather than reading as "ungrouped" the moment this ships.</para>
///
/// <para><b><c>worker_schedules.buffers_count_toward_service_duration</c></b> - the tenant's own
/// call on whether a multi-slot run's internal buffers count toward satisfying a service's
/// duration (<see cref="WorkerSchedule.BuffersCountTowardServiceDuration"/>'s own remarks carry
/// the arithmetic). Defaults <see langword="true"/> at the column, matching the aggregate's own
/// default, so an existing schedule - which predates the setting and never chose either reading -
/// lands on the author's own stated default rather than an arbitrary one.</para>
///
/// <para><b>Order, and why the backfill sits where it does.</b> <c>dotnet ef migrations add</c>
/// produced <c>AddColumn</c> for both columns, then the index, then the foreign key; the backfill
/// is inserted between the column and the index/constraint additions, on purpose - it has to run
/// after <c>booking_id</c> exists and before the foreign key is added, though in this particular
/// case the ordering is not load-bearing (every backfilled value is a row's own <c>id</c>, which
/// trivially satisfies a self-referencing foreign key) and is kept anyway for the same reason
/// `20-14`'s own migration keeps its insert before its drop: a reader should not have to prove an
/// ordering is safe that the file could simply not need proving.</para>
///
/// <para><b>Which rows are backfilled, and why that predicate.</b> <c>customer_id is not null</c>
/// is the same fact <c>PendingBookingReadStore</c>'s own remarks already rely on - only
/// <see cref="Event.Claim"/> ever sets it, together with the status, so a row carrying one has
/// necessarily passed through a claim and is exactly the set of rows `20-18`'s grouping now needs
/// a non-null <c>booking_id</c> on. An <see cref="EventStatus.Available"/> or
/// <see cref="EventStatus.Blocked"/> row was never claimed and correctly keeps
/// <c>booking_id null</c> - inventing one would be a fact about a booking that does not exist.
/// </para>
/// </summary>
public partial class Stage20BookingIdAndBuffersCountTowardServiceDuration : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "buffers_count_toward_service_duration",
            table: "worker_schedules",
            type: "boolean",
            nullable: false,
            defaultValue: true);

        migrationBuilder.AddColumn<Guid>(
            name: "booking_id",
            table: "events",
            type: "uuid",
            nullable: true);

        // Every already-claimed row becomes its own anchor - the identical default
        // Event.Claim itself falls back to when a caller (every caller before `20-18`) never
        // named one. A row this predicate does not match (Available, Blocked) is correctly left
        // at booking_id = null: it was never claimed, so it has no booking to belong to.
        migrationBuilder.Sql(
            """
            UPDATE events
            SET booking_id = id
            WHERE customer_id IS NOT NULL
              AND booking_id IS NULL
            """);

        migrationBuilder.CreateIndex(
            name: "ix_events_booking_id",
            table: "events",
            column: "booking_id",
            filter: "booking_id IS NOT NULL");

        migrationBuilder.AddForeignKey(
            name: "FK_events_events_booking_id",
            table: "events",
            column: "booking_id",
            principalTable: "events",
            principalColumn: "id",
            onDelete: ReferentialAction.Restrict);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_events_events_booking_id",
            table: "events");

        migrationBuilder.DropIndex(
            name: "ix_events_booking_id",
            table: "events");

        migrationBuilder.DropColumn(
            name: "buffers_count_toward_service_duration",
            table: "worker_schedules");

        migrationBuilder.DropColumn(
            name: "booking_id",
            table: "events");
    }
}
