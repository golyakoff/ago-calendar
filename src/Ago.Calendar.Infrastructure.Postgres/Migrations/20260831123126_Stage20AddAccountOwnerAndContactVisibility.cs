using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Calendar.Infrastructure.Postgres.Migrations;

/// <summary>
/// `20-12`: <c>Operator.IsAccountOwner</c> and <c>RoleAssignment.GrantsCustomerRead</c> - one migration
/// for both, per the item's own budget of exactly one. Two hand-written backfills follow the two
/// <c>AddColumn</c> calls EF generated; both are reversible (the <c>Down</c> just drops the columns,
/// so nothing hand-written needs its own undo) and both are explained here rather than left as raw SQL
/// a future reader has to reverse-engineer.
/// </summary>
public partial class Stage20AddAccountOwnerAndContactVisibility : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "is_account_owner",
            table: "operators",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        // Backfill: mark the earliest-created operator of every tenant as its account owner.
        //
        // `operators` carries no `created_at` column, so "earliest created" is not literally stored -
        // but `IIdGenerator`'s real implementation (Ago.Platform.Kernel.UuidV7Generator) mints UUIDv7
        // ids, and a UUIDv7's own leading bits are a millisecond timestamp, so sorting by `id` sorts
        // by creation order. That is what makes `id` a valid proxy for "first operator" here: it
        // would not be if ids were random (UUIDv4) or sequential-but-unordered.
        //
        // `DISTINCT ON (tenant_id) ... ORDER BY tenant_id, id`, not `MIN(id)` - stock PostgreSQL has
        // no `min(uuid)`/`max(uuid)` aggregate (confirmed against a real 17-alpine container while
        // building this migration's own test: `42883: function min(uuid) does not exist`), only the
        // ordinary btree comparison operators `uuid` already supports. `DISTINCT ON` needs only
        // `ORDER BY`, so it reaches the identical "smallest id per tenant" row without an aggregate
        // that does not exist for this column type.
        //
        // A tenant with no operators at all contributes no row to the subquery, so this only ever
        // touches tenants that already have at least one.
        migrationBuilder.Sql(
            """
            UPDATE operators
            SET is_account_owner = true
            WHERE id IN (
                SELECT DISTINCT ON (tenant_id) id
                FROM operators
                ORDER BY tenant_id, id
            )
            """);

        migrationBuilder.AddColumn<bool>(
            name: "grants_customer_read",
            table: "operator_roles",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        // Backfill: RoleAssignment.GrantsCustomerRead is a snapshot taken at grant time from now on,
        // but every operator_roles row already in the table predates that write path. Recomputed once
        // here by joining back to roles.permissions - the one and only place this migration needs to
        // read a role's actual permission set, because every grant from this point on carries its own
        // answer with it (RoleAssignment.GrantsCustomerRead's own remarks on why that snapshot cannot
        // go stale).
        migrationBuilder.Sql(
            """
            UPDATE operator_roles orl
            SET grants_customer_read = true
            FROM roles r
            WHERE r.id = orl.role_id
              AND 'customer:read' = ANY(r.permissions)
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "is_account_owner",
            table: "operators");

        migrationBuilder.DropColumn(
            name: "grants_customer_read",
            table: "operator_roles");
    }
}
