using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Calendar.Infrastructure.Postgres.Migrations;

/// <summary>
/// `23-12`/`adr/0123`: three pieces of schema for one item's own budget - the projection of the
/// account's contact-visibility rung (EF-mapped, <c>ContactVisibilityProjectionConfiguration</c>), the
/// operator's own "I called and it is them" mark on <c>customers</c>
/// (<c>Customer.PhoneConfirmedByOperatorAt</c>), and <c>contact_phone_reveals</c> - hand-written
/// below because it backs
/// <c>Ago.Calendar.Application.Abstractions.IContactPhoneRevealRepository</c>, a raw-Npgsql port with
/// no EF entity behind it (that port's own remarks explain why: no aggregate, no invariant beyond one
/// row per event, the identical shape `ago-chat`'s own <c>contact_reveals</c> migration already
/// establishes for its account-side twin).
/// </summary>
public partial class Stage23AddContactVisibilityProjectionAndPhoneReveals : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "operator_confirmed_phone_at",
            table: "customers",
            type: "timestamptz",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "contact_visibility_projections",
            columns: table => new
            {
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                rung = table.Column<string>(type: "text", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_contact_visibility_projections", x => x.tenant_id);
            });

        // `23-12`: no EF entity - IContactPhoneRevealRepository's own remarks on why this is raw
        // Npgsql, the identical "one row per event, no aggregate" shape `ago-chat`'s own
        // `contact_reveals` table already takes. No foreign key on tenant_id or customer_id, for the
        // same adr/0111/adr/0112/adr/0113 reasoning that table's own remarks give: this record must
        // survive whatever erases the customer or the tenant it names, or the one question it exists
        // to answer ("who revealed this before it was deleted") would lose its own evidence to the
        // very process the question is about.
        migrationBuilder.CreateTable(
            name: "contact_phone_reveals",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                occurred_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                operator_id = table.Column<Guid>(type: "uuid", nullable: false),
                surface = table.Column<string>(type: "text", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_contact_phone_reveals", x => x.id);
            });

        // `23-12`: the tenant's own keyset read (IContactPhoneRevealRepository.ListForTenantAsync)
        // orders by id desc within a tenant - this composite index serves that query directly, the
        // same shape `ago-chat`'s own `ix_contact_reveals_site_id_id` already establishes.
        migrationBuilder.CreateIndex(
            name: "ix_contact_phone_reveals_tenant_id_id",
            table: "contact_phone_reveals",
            columns: ["tenant_id", "id"]);

        // `ContactPhoneRevealPruneJob`'s own retention query filters and orders by occurred_at across
        // every tenant - this index serves that scan directly.
        migrationBuilder.CreateIndex(
            name: "ix_contact_phone_reveals_occurred_at",
            table: "contact_phone_reveals",
            column: "occurred_at");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "contact_phone_reveals");

        migrationBuilder.DropTable(
            name: "contact_visibility_projections");

        migrationBuilder.DropColumn(
            name: "operator_confirmed_phone_at",
            table: "customers");
    }
}
