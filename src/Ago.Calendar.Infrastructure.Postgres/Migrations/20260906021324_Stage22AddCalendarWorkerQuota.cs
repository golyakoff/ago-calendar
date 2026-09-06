using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Calendar.Infrastructure.Postgres.Migrations;

/// <inheritdoc />
public partial class Stage22AddCalendarWorkerQuota : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "worker_quota",
            table: "tenants",
            type: "integer",
            nullable: false,
            defaultValue: 0);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "worker_quota",
            table: "tenants");
    }
}
