using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Calendar.Infrastructure.Postgres.Migrations;

/// <inheritdoc />
public partial class Stage25AddChatBookingTaskLastReplyValue : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "last_applied_value",
            table: "chat_booking_tasks",
            type: "character varying(64)",
            maxLength: 64,
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "last_applied_value",
            table: "chat_booking_tasks");
    }
}
