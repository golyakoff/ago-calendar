using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Calendar.Infrastructure.Postgres.Migrations;

/// <inheritdoc />
public partial class Stage22AddSuspensionLease : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "suspension_valid_until",
            table: "tenants",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "ix_tenants_suspension_valid_until",
            table: "tenants",
            column: "suspension_valid_until",
            filter: "suspension_valid_until IS NOT NULL");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "ix_tenants_suspension_valid_until",
            table: "tenants");

        migrationBuilder.DropColumn(
            name: "suspension_valid_until",
            table: "tenants");
    }
}
