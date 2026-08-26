using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Calendar.Infrastructure.Postgres.Migrations;

/// <summary>
/// `20-06`: the tenant gains a public embed surface - the key a script tag names it by, and the
/// page origins allowed to use it (`5-01`'s model, adapted from site to tenant).
///
/// <para><b>The only schema change this item makes</b>, and it is catalogue-only: two added columns
/// and two indexes, no rewrite of any existing row's data.</para>
///
/// <para><b>The unique index on an empty default is safe here and would not be everywhere</b> -
/// worth stating rather than discovering. <c>public_key</c> is <c>NOT NULL</c> with a <c>''</c>
/// default, so a table that already held two tenants would fail this migration on the unique index
/// rather than on the column. <c>tenants</c> has no rows in any deployment: this product has never
/// been deployed, and the endpoint that creates the first tenant arrives in this same item. A future
/// change of this shape against a populated table needs a backfill between the two statements, and
/// the honest reason there is none here is that there is nothing to fill.</para>
/// </summary>
public partial class Stage20AddTenantEmbedSurface : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string[]>(
            name: "allowed_origins",
            table: "tenants",
            type: "text[]",
            nullable: false,
            defaultValue: new string[0]);

        migrationBuilder.AddColumn<string>(
            name: "public_key",
            table: "tenants",
            type: "character varying(64)",
            maxLength: 64,
            nullable: false,
            defaultValue: "");

        // GIN, so that layer 1's `@origin = ANY(allowed_origins)` is an index probe rather than a
        // sequential scan of every tenant on every CORS preflight.
        migrationBuilder.CreateIndex(
            name: "ix_tenants_allowed_origins",
            table: "tenants",
            column: "allowed_origins")
            .Annotation("Npgsql:IndexMethod", "gin");

        migrationBuilder.CreateIndex(
            name: "ux_tenants_public_key",
            table: "tenants",
            column: "public_key",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "ix_tenants_allowed_origins",
            table: "tenants");

        migrationBuilder.DropIndex(
            name: "ux_tenants_public_key",
            table: "tenants");

        migrationBuilder.DropColumn(
            name: "allowed_origins",
            table: "tenants");

        migrationBuilder.DropColumn(
            name: "public_key",
            table: "tenants");
    }
}
