using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Calendar.Infrastructure.Postgres.Migrations;

/// <inheritdoc />
public partial class Stage23AddChatSourcedCustomers : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "ux_customers_tenant_phone",
            table: "customers");

        migrationBuilder.AddColumn<string>(
            name: "source",
            table: "customers",
            type: "character varying(16)",
            maxLength: 16,
            nullable: false,
            defaultValue: "Booking");

        migrationBuilder.AddColumn<Guid>(
            name: "source_contact_id",
            table: "customers",
            type: "uuid",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "ux_customers_tenant_phone",
            table: "customers",
            columns: new[] { "tenant_id", "phone" },
            unique: true,
            filter: "source = 'Booking'");

        migrationBuilder.CreateIndex(
            name: "ux_customers_tenant_source_contact",
            table: "customers",
            columns: new[] { "tenant_id", "source_contact_id" },
            unique: true,
            filter: "source_contact_id IS NOT NULL");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "ux_customers_tenant_phone",
            table: "customers");

        migrationBuilder.DropIndex(
            name: "ux_customers_tenant_source_contact",
            table: "customers");

        migrationBuilder.DropColumn(
            name: "source",
            table: "customers");

        migrationBuilder.DropColumn(
            name: "source_contact_id",
            table: "customers");

        migrationBuilder.CreateIndex(
            name: "ux_customers_tenant_phone",
            table: "customers",
            columns: new[] { "tenant_id", "phone" },
            unique: true);
    }
}
