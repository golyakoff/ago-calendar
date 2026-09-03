using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Calendar.Infrastructure.Postgres.Migrations;

/// <summary>
/// `22-05`/`adr/0093`: three identity tables drop, one projection table appears. Destructive, and
/// said so explicitly rather than left to the diff: <c>operator_roles</c>, <c>operators</c> and
/// <c>roles</c> are dropped with everything they hold - every existing operator row, every role and
/// every grant - because this product no longer holds identity of its own; the same facts now arrive
/// through <c>role_assignment_projections</c>, replicated from the account side over the outbox
/// (<c>RoleAssignmentsChanged</c>). This is safe today only because it is safe today: `ago_calendar`
/// holds zero rows in production (`22-03`'s own report), so nothing is actually destroyed by running
/// this - the destructiveness is a property of the schema change, not of this particular run.
///
/// <para><b>Order matters</b> (CLAUDE.md's own instruction to this item): <c>operator_roles</c>
/// (the child, with foreign keys into both other tables) drops before <c>operators</c>/<c>roles</c>
/// (the parents) - the order Postgres requires anyway, since a parent cannot be dropped while a
/// foreign key still references it.</para>
/// </summary>
public partial class Stage22RemoveOperatorIdentityAddRoleAssignmentProjections : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "operator_roles");

        migrationBuilder.DropTable(
            name: "operators");

        migrationBuilder.DropTable(
            name: "roles");

        migrationBuilder.CreateTable(
            name: "role_assignment_projections",
            columns: table => new
            {
                operator_id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                external_subject_id = table.Column<string>(type: "text", nullable: false),
                permissions = table.Column<List<string>>(type: "text[]", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_role_assignment_projections", x => new { x.operator_id, x.tenant_id });
            });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "role_assignment_projections");

        migrationBuilder.CreateTable(
            name: "operators",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                external_subject_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                invited_email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                is_account_owner = table.Column<bool>(type: "boolean", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_operators", x => x.id);
                table.ForeignKey(
                    name: "FK_operators_tenants_tenant_id",
                    column: x => x.tenant_id,
                    principalTable: "tenants",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "roles",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                permissions = table.Column<string[]>(type: "text[]", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_roles", x => x.id);
                table.ForeignKey(
                    name: "FK_roles_tenants_tenant_id",
                    column: x => x.tenant_id,
                    principalTable: "tenants",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "operator_roles",
            columns: table => new
            {
                operator_id = table.Column<Guid>(type: "uuid", nullable: false),
                role_id = table.Column<Guid>(type: "uuid", nullable: false),
                grants_customer_read = table.Column<bool>(type: "boolean", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_operator_roles", x => new { x.operator_id, x.role_id });
                table.ForeignKey(
                    name: "FK_operator_roles_operators_operator_id",
                    column: x => x.operator_id,
                    principalTable: "operators",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_operator_roles_roles_role_id",
                    column: x => x.role_id,
                    principalTable: "roles",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_operator_roles_role_id",
            table: "operator_roles",
            column: "role_id");

        migrationBuilder.CreateIndex(
            name: "ix_operators_invited_email",
            table: "operators",
            column: "invited_email");

        migrationBuilder.CreateIndex(
            name: "IX_operators_tenant_id",
            table: "operators",
            column: "tenant_id");

        migrationBuilder.CreateIndex(
            name: "ux_operators_external_subject_id",
            table: "operators",
            column: "external_subject_id",
            unique: true,
            filter: "external_subject_id IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "ux_roles_tenant_name",
            table: "roles",
            columns: new[] { "tenant_id", "name" },
            unique: true);
    }
}
