using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Calendar.Infrastructure.Postgres.Migrations;

/// <summary>
/// `23-60`/`adr/0147`: the other half of that ADR's own choice - a phone match a human now acts on.
/// Two tombstone columns on <c>customers</c> (EF-mapped, <c>CustomerConfiguration</c>), plus
/// <c>customer_merges</c> - hand-written below because it backs
/// <c>Ago.Calendar.Application.Abstractions.ICustomerMergeReadStore</c>, a raw-Npgsql port with no EF
/// entity behind it, the identical shape `Stage23AddContactVisibilityProjectionAndPhoneReveals`
/// already establishes for <c>contact_phone_reveals</c>.
///
/// <para><b><c>customer_merges</c> carries real foreign keys, unlike that table - a deliberate
/// divergence, not an oversight.</b> `docs/architecture/personal-data.md`'s own distinction: some
/// audit rows are evidence that must outlive the tenant they describe
/// (<c>contact_phone_reveals</c>/<c>access_records</c>, no foreign key, so a tenant's own erasure
/// cannot take the evidence of what our staff did to it), and some are a tenant's own internal
/// administration log about an act taken entirely within that tenant
/// (<c>role_change_records</c>/<c>team_message_removals</c>, foreign-keyed, cascading with the tenant
/// because a row that outlived the tenant it describes would be an unreachable fragment of an
/// operator's personal data with nothing left to attach it to). A merge is the second kind: "who
/// merged which of our own customers, and when" is the tenant's own question about its own record,
/// not evidence AGO itself might need to answer for after the tenant is gone - so
/// <c>tenant_id</c>/<c>survivor_customer_id</c>/<c>absorbed_customer_id</c> all cascade, and only
/// <c>operator_id</c> carries none, the identical "no local <c>operators</c> table left to reference"
/// reason <c>contact_phone_reveals.operator_id</c> already has (`22-05`/`adr/0093`).</para>
/// </summary>
public partial class Stage23AddCustomerMergesAndTombstone : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "merged_at",
            table: "customers",
            type: "timestamptz",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "merged_into_customer_id",
            table: "customers",
            type: "uuid",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_customers_merged_into_customer_id",
            table: "customers",
            column: "merged_into_customer_id");

        migrationBuilder.AddForeignKey(
            name: "FK_customers_customers_merged_into_customer_id",
            table: "customers",
            column: "merged_into_customer_id",
            principalTable: "customers",
            principalColumn: "id",
            onDelete: ReferentialAction.Cascade);

        // `23-60`: no EF entity - ICustomerMergeReadStore's own remarks on why this is raw Npgsql, the
        // "one row per event, no aggregate" shape contact_phone_reveals already takes. Unlike that
        // table, this one does carry foreign keys - this migration's own remarks explain why a merge
        // record is a tenant-internal administration log rather than evidence that must survive the
        // tenant's own erasure.
        migrationBuilder.CreateTable(
            name: "customer_merges",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                survivor_customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                absorbed_customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                operator_id = table.Column<Guid>(type: "uuid", nullable: false),
                bookings_moved = table.Column<int>(type: "integer", nullable: false),
                merged_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_customer_merges", x => x.id);
                table.ForeignKey(
                    name: "FK_customer_merges_tenants_tenant_id",
                    column: x => x.tenant_id,
                    principalTable: "tenants",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_customer_merges_customers_survivor_customer_id",
                    column: x => x.survivor_customer_id,
                    principalTable: "customers",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_customer_merges_customers_absorbed_customer_id",
                    column: x => x.absorbed_customer_id,
                    principalTable: "customers",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        // The tenant's own keyset read (ICustomerMergeReadStore.ListForTenantAsync) orders by id desc
        // within a tenant - the identical shape ix_contact_phone_reveals_tenant_id_id already
        // establishes for its own sibling audit trail.
        migrationBuilder.CreateIndex(
            name: "ix_customer_merges_tenant_id_id",
            table: "customer_merges",
            columns: ["tenant_id", "id"]);

        // Neither column is ever looked up by itself in this item's own scope (no "show me every
        // merge this customer was ever part of" screen exists), but both carry a foreign key, and an
        // unindexed foreign key column is a sequential scan waiting for the day the cascade above
        // actually fires on a tenant with a large merge history.
        migrationBuilder.CreateIndex(
            name: "ix_customer_merges_survivor_customer_id",
            table: "customer_merges",
            column: "survivor_customer_id");
        migrationBuilder.CreateIndex(
            name: "ix_customer_merges_absorbed_customer_id",
            table: "customer_merges",
            column: "absorbed_customer_id");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "customer_merges");

        migrationBuilder.DropForeignKey(
            name: "FK_customers_customers_merged_into_customer_id",
            table: "customers");

        migrationBuilder.DropIndex(
            name: "IX_customers_merged_into_customer_id",
            table: "customers");

        migrationBuilder.DropColumn(
            name: "merged_at",
            table: "customers");

        migrationBuilder.DropColumn(
            name: "merged_into_customer_id",
            table: "customers");
    }
}
