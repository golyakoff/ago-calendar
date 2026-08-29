using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Calendar.Infrastructure.Postgres.Migrations;

/// <inheritdoc />
public partial class Stage20AddChatBookingTasks : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "chat_booking_tasks",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                calendar_id = table.Column<Guid>(type: "uuid", nullable: false),
                service_id = table.Column<Guid>(type: "uuid", nullable: true),
                worker_id = table.Column<Guid>(type: "uuid", nullable: true),
                event_id = table.Column<Guid>(type: "uuid", nullable: true),
                phone = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                state = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_chat_booking_tasks", x => x.id);
                table.ForeignKey(
                    name: "FK_chat_booking_tasks_calendars_calendar_id",
                    column: x => x.calendar_id,
                    principalTable: "calendars",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_chat_booking_tasks_tenants_tenant_id",
                    column: x => x.tenant_id,
                    principalTable: "tenants",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_chat_booking_tasks_calendar_id",
            table: "chat_booking_tasks",
            column: "calendar_id");

        migrationBuilder.CreateIndex(
            name: "IX_chat_booking_tasks_tenant_id",
            table: "chat_booking_tasks",
            column: "tenant_id");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "chat_booking_tasks");
    }
}
