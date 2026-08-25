using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Calendar.Infrastructure.Postgres.Migrations;

/// <inheritdoc />
public partial class Stage20CreateCalendarSchema : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "inbox",
            columns: table => new
            {
                message_id = table.Column<Guid>(type: "uuid", nullable: false),
                consumer = table.Column<string>(type: "text", nullable: false),
                processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_inbox", x => new { x.message_id, x.consumer });
            });

        migrationBuilder.CreateTable(
            name: "outbox",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                type = table.Column<string>(type: "text", nullable: false),
                version = table.Column<int>(type: "integer", nullable: false),
                payload = table.Column<string>(type: "jsonb", nullable: false),
                partition_key = table.Column<string>(type: "text", nullable: false),
                correlation_id = table.Column<Guid>(type: "uuid", nullable: false),
                trace_context = table.Column<string>(type: "text", nullable: true),
                published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                attempts = table.Column<int>(type: "integer", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_outbox", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "tenants",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_tenants", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "calendars",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                time_zone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                buffer_minutes = table.Column<int>(type: "integer", nullable: false),
                is_published = table.Column<bool>(type: "boolean", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_calendars", x => x.id);
                table.ForeignKey(
                    name: "FK_calendars_tenants_tenant_id",
                    column: x => x.tenant_id,
                    principalTable: "tenants",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "customers",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                phone = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                notes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                no_show_count = table.Column<int>(type: "integer", nullable: false),
                first_seen_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                last_seen_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_customers", x => x.id);
                table.ForeignKey(
                    name: "FK_customers_tenants_tenant_id",
                    column: x => x.tenant_id,
                    principalTable: "tenants",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "operators",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                external_subject_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true)
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
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
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
            name: "services",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                duration_minutes = table.Column<int>(type: "integer", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_services", x => x.id);
                table.ForeignKey(
                    name: "FK_services_tenants_tenant_id",
                    column: x => x.tenant_id,
                    principalTable: "tenants",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "workers",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                is_active = table.Column<bool>(type: "boolean", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_workers", x => x.id);
                table.ForeignKey(
                    name: "FK_workers_tenants_tenant_id",
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
                role_id = table.Column<Guid>(type: "uuid", nullable: false)
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

        migrationBuilder.CreateTable(
            name: "calendar_workers",
            columns: table => new
            {
                worker_id = table.Column<Guid>(type: "uuid", nullable: false),
                calendar_id = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_calendar_workers", x => new { x.calendar_id, x.worker_id });
                table.ForeignKey(
                    name: "FK_calendar_workers_calendars_calendar_id",
                    column: x => x.calendar_id,
                    principalTable: "calendars",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_calendar_workers_workers_worker_id",
                    column: x => x.worker_id,
                    principalTable: "workers",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "events",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                calendar_id = table.Column<Guid>(type: "uuid", nullable: false),
                worker_id = table.Column<Guid>(type: "uuid", nullable: false),
                service_id = table.Column<Guid>(type: "uuid", nullable: true),
                customer_id = table.Column<Guid>(type: "uuid", nullable: true),
                starts_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                ends_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                local_date = table.Column<DateOnly>(type: "date", nullable: false),
                status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                confirmation_deadline = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_events", x => x.id);
                table.ForeignKey(
                    name: "FK_events_calendars_calendar_id",
                    column: x => x.calendar_id,
                    principalTable: "calendars",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_events_customers_customer_id",
                    column: x => x.customer_id,
                    principalTable: "customers",
                    principalColumn: "id");
                table.ForeignKey(
                    name: "FK_events_services_service_id",
                    column: x => x.service_id,
                    principalTable: "services",
                    principalColumn: "id");
                table.ForeignKey(
                    name: "FK_events_tenants_tenant_id",
                    column: x => x.tenant_id,
                    principalTable: "tenants",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_events_workers_worker_id",
                    column: x => x.worker_id,
                    principalTable: "workers",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "worker_services",
            columns: table => new
            {
                worker_id = table.Column<Guid>(type: "uuid", nullable: false),
                service_id = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_worker_services", x => new { x.worker_id, x.service_id });
                table.ForeignKey(
                    name: "FK_worker_services_services_service_id",
                    column: x => x.service_id,
                    principalTable: "services",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_worker_services_workers_worker_id",
                    column: x => x.worker_id,
                    principalTable: "workers",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "working_hours_rules",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                worker_id = table.Column<Guid>(type: "uuid", nullable: false),
                calendar_id = table.Column<Guid>(type: "uuid", nullable: false),
                day_of_week = table.Column<string>(type: "character varying(9)", maxLength: 9, nullable: false),
                starts_at = table.Column<TimeOnly>(type: "time", nullable: false),
                ends_at = table.Column<TimeOnly>(type: "time", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_working_hours_rules", x => x.id);
                table.ForeignKey(
                    name: "FK_working_hours_rules_calendars_calendar_id",
                    column: x => x.calendar_id,
                    principalTable: "calendars",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_working_hours_rules_workers_worker_id",
                    column: x => x.worker_id,
                    principalTable: "workers",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_calendar_workers_worker_id",
            table: "calendar_workers",
            column: "worker_id");

        migrationBuilder.CreateIndex(
            name: "ix_calendars_published",
            table: "calendars",
            column: "tenant_id",
            filter: "is_published");

        migrationBuilder.CreateIndex(
            name: "ux_customers_tenant_phone",
            table: "customers",
            columns: new[] { "tenant_id", "phone" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_events_available",
            table: "events",
            columns: new[] { "calendar_id", "starts_at" },
            filter: "status = 'Available'");

        migrationBuilder.CreateIndex(
            name: "IX_events_customer_id",
            table: "events",
            column: "customer_id");

        migrationBuilder.CreateIndex(
            name: "ix_events_pending_confirmation",
            table: "events",
            columns: new[] { "tenant_id", "confirmation_deadline" },
            filter: "status = 'PendingConfirmation'");

        migrationBuilder.CreateIndex(
            name: "IX_events_service_id",
            table: "events",
            column: "service_id");

        migrationBuilder.CreateIndex(
            name: "IX_events_worker_id",
            table: "events",
            column: "worker_id");

        migrationBuilder.CreateIndex(
            name: "IX_operator_roles_role_id",
            table: "operator_roles",
            column: "role_id");

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
            name: "ix_outbox_unpublished",
            table: "outbox",
            column: "id",
            filter: "published_at IS NULL");

        migrationBuilder.CreateIndex(
            name: "ux_roles_tenant_name",
            table: "roles",
            columns: new[] { "tenant_id", "name" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_services_tenant_id",
            table: "services",
            column: "tenant_id");

        migrationBuilder.CreateIndex(
            name: "IX_worker_services_service_id",
            table: "worker_services",
            column: "service_id");

        migrationBuilder.CreateIndex(
            name: "IX_workers_tenant_id",
            table: "workers",
            column: "tenant_id");

        migrationBuilder.CreateIndex(
            name: "ix_working_hours_rules_calendar_worker",
            table: "working_hours_rules",
            columns: new[] { "calendar_id", "worker_id" });

        migrationBuilder.CreateIndex(
            name: "IX_working_hours_rules_worker_id",
            table: "working_hours_rules",
            column: "worker_id");

        // ---------------------------------------------------------------------------------
        // Hand-written, because EF cannot express it and because it is the single most
        // important line in this schema: the no-double-booking guarantee.
        //
        // The rule is "one worker is never in two places at once". An aggregate cannot enforce
        // it - Event can only see itself, so deciding whether some *other* row overlaps would
        // mean reading the neighbours and then writing, which two concurrent materialisation
        // runs or a double-submitted manual edit walk straight through. Postgres can enforce it
        // atomically, so it is declared here (data-model.md: "anything enforcing a guarantee
        // gets a constraint, not just application code").
        //
        // Why GiST and not a unique index: uniqueness compares values for equality, and
        // overlap is not equality - 10:00-11:00 and 10:30-11:30 are different values that must
        // still collide. `tstzrange(...) WITH &&` is the operator that says so. btree_gist is
        // what lets the ordinary equality on worker_id sit in the same GiST index next to it.
        //
        // '[)' - half-open, matching TimeSlot's own comparison exactly, so back-to-back slots
        // are adjacent rather than overlapping. Closed bounds here would reject every ordinary
        // working day at the first slot boundary.
        //
        // WHERE (status <> 'Cancelled') - a cancelled booking releases the worker's time, so it
        // must stop blocking. Everything else occupies it, Blocked and NoShow included: a
        // no-show still happened in the diary, and a block is exactly a statement that the time
        // is taken. The predicate also keeps the index proportional to live rows rather than to
        // every booking the tenant has ever cancelled.
        //
        // Postgres-specific, and named as such for the Stage 9 friction list in data-model.md:
        // MySQL has no exclusion constraints and no range types, so a MySQL adapter would need
        // a different mechanism entirely (a lock or a serialized transaction), not a translated
        // DDL statement.
        // ---------------------------------------------------------------------------------
        migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS btree_gist;");
        migrationBuilder.Sql(
            """
            ALTER TABLE events
                ADD CONSTRAINT ex_events_worker_no_overlap
                EXCLUDE USING gist (
                    worker_id WITH =,
                    tstzrange(starts_at, ends_at, '[)') WITH &&
                )
                WHERE (status <> 'Cancelled');
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Fully reversible (data-model.md's default, and the bar this item is held to). Dropping
        // `events` would take the constraint with it, but dropping it explicitly first keeps the
        // Down readable as the exact inverse of the Up rather than as a side effect.
        migrationBuilder.Sql("ALTER TABLE events DROP CONSTRAINT IF EXISTS ex_events_worker_no_overlap;");

        migrationBuilder.DropTable(
            name: "calendar_workers");

        migrationBuilder.DropTable(
            name: "events");

        migrationBuilder.DropTable(
            name: "inbox");

        migrationBuilder.DropTable(
            name: "operator_roles");

        migrationBuilder.DropTable(
            name: "outbox");

        migrationBuilder.DropTable(
            name: "worker_services");

        migrationBuilder.DropTable(
            name: "working_hours_rules");

        migrationBuilder.DropTable(
            name: "customers");

        migrationBuilder.DropTable(
            name: "operators");

        migrationBuilder.DropTable(
            name: "roles");

        migrationBuilder.DropTable(
            name: "services");

        migrationBuilder.DropTable(
            name: "calendars");

        migrationBuilder.DropTable(
            name: "workers");

        migrationBuilder.DropTable(
            name: "tenants");

        // Created by this migration, so it is this migration's to remove. Safe here and only
        // here: this is the schema's first migration, so a Down that runs at all leaves an empty
        // database with nothing else that could depend on the extension.
        migrationBuilder.Sql("DROP EXTENSION IF EXISTS btree_gist;");
    }
}
