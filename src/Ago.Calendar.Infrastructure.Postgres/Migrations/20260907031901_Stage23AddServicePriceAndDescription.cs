using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Calendar.Infrastructure.Postgres.Migrations;

/// <inheritdoc />
public partial class Stage23AddServicePriceAndDescription : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "description",
            table: "services",
            type: "character varying(1000)",
            maxLength: 1000,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "price_currency_code",
            table: "services",
            type: "character varying(3)",
            maxLength: 3,
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "price_is_from",
            table: "services",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<int>(
            name: "price_minor_units",
            table: "services",
            type: "integer",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "description",
            table: "services");

        migrationBuilder.DropColumn(
            name: "price_currency_code",
            table: "services");

        migrationBuilder.DropColumn(
            name: "price_is_from",
            table: "services");

        migrationBuilder.DropColumn(
            name: "price_minor_units",
            table: "services");
    }
}
