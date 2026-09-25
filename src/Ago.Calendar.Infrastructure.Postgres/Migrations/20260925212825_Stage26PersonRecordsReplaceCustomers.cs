using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Calendar.Infrastructure.Postgres.Migrations;

/// <summary>
/// `adr/0184` (option B): the calendar deletes <c>customers</c> as a person store. What replaces it is
/// <c>person_records</c> - a thin operational record keyed by the opaque person id, holding only the
/// facts CLAUDE.md rule 8 makes this product keep local (the phone booked with, its two verification
/// marks, the no-show count, first/last seen). <c>events.customer_id</c> goes; <c>events.person_id</c>
/// (added by `26-136`) becomes the booking's one person reference and gains a real foreign key. The
/// `23-60` merge ledger (<c>customer_merges</c>, hand-written in its own migration and so invisible to
/// the EF snapshot) is dropped with the merge it recorded, and <c>contact_phone_reveals.customer_id</c>
/// is renamed to <c>person_id</c> - the same ids, under the name they now carry everywhere else.
///
/// <para><b>One reshape, not an expand/contract dance - and it still carries the rows it finds.</b>
/// There are no real tenants (the author's own standing note at the time of `adr/0184`), so the
/// whole change is one migration. But a migration that <em>fails</em> against a database with demo
/// rows is worse than one that carries them, so the steps below are ordered to keep every existing
/// booking's person reference intact: create the new table; give every old customer a record under
/// its own id (a customer id is as good an opaque person id as any - what matters is that
/// <c>events</c> rows keep resolving); give every event that `26-136` already stamped with a chat
/// visitor id a record under <em>that</em> id, taking the phone from the customer row it was booked
/// against; then point every remaining event's <c>person_id</c> at its old customer's id; only then
/// drop the copy. Every data statement is a no-op on an empty database, which is what the integration
/// suite migrates.</para>
///
/// <para><b>What is deliberately not carried: <c>display_name</c> and <c>notes</c>.</b> Those are the
/// person copy this decision deletes. A chat-origin booking's name already lives on chat's own Person;
/// a demo customer's typed name has no Person to move to and is dropped with the table - a loss the
/// author accepted for a zero-tenant deployment when choosing one reshape over three slices.</para>
///
/// <para><b><see cref="Down"/> restores the shape, never the data.</b> A reverse migration can
/// recreate <c>customers</c> and <c>customer_merges</c> empty and give <c>events</c> its
/// <c>customer_id</c> column back, but nothing can reconstruct rows this migration dropped. It exists
/// so <c>dotnet ef</c> can walk the history; it is not a rollback plan.</para>
/// </summary>
public partial class Stage26PersonRecordsReplaceCustomers : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // 1. The new table first, so the data steps below have somewhere to write.
        migrationBuilder.CreateTable(
            name: "person_records",
            columns: table => new
            {
                person_id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                phone = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                no_show_count = table.Column<int>(type: "integer", nullable: false),
                first_seen_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                last_seen_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                phone_verified_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                operator_confirmed_phone_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_person_records", x => x.person_id);
                table.ForeignKey(
                    name: "FK_person_records_tenants_tenant_id",
                    column: x => x.tenant_id,
                    principalTable: "tenants",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "ix_person_records_tenant_last_seen",
            table: "person_records",
            columns: new[] { "tenant_id", "last_seen_at" });

        migrationBuilder.CreateIndex(
            name: "ix_person_records_tenant_phone",
            table: "person_records",
            columns: new[] { "tenant_id", "phone" });

        // 2. Every old customer becomes a record under its own id - tombstoned (merged-away) rows
        //    included, harmlessly: nothing points at them any more, and the tenant cascade takes them
        //    with everything else. The customer id doubles as the opaque person id from here on.
        migrationBuilder.Sql(
            """
            INSERT INTO person_records
                (person_id, tenant_id, phone, no_show_count, first_seen_at, last_seen_at, phone_verified_at, operator_confirmed_phone_at)
            SELECT c.id, c.tenant_id, c.phone, c.no_show_count, c.first_seen_at, c.last_seen_at,
                   c.phone_verified_at, c.operator_confirmed_phone_at
            FROM customers c
            """);

        // 3. An event `26-136` already stamped with chat's own visitor id keeps that id: it gets a
        //    record of its own, with the phone (and marks) taken from the customer row the booking
        //    was actually made against. DISTINCT ON keeps one row per person id when the same visitor
        //    booked several times.
        migrationBuilder.Sql(
            """
            INSERT INTO person_records
                (person_id, tenant_id, phone, no_show_count, first_seen_at, last_seen_at, phone_verified_at, operator_confirmed_phone_at)
            SELECT DISTINCT ON (e.person_id)
                   e.person_id, e.tenant_id, c.phone, 0, c.first_seen_at, c.last_seen_at,
                   c.phone_verified_at, c.operator_confirmed_phone_at
            FROM events e
            JOIN customers c ON c.id = e.customer_id
            WHERE e.person_id IS NOT NULL
              AND NOT EXISTS (SELECT 1 FROM person_records p WHERE p.person_id = e.person_id)
            ORDER BY e.person_id, e.created_at DESC
            """);

        // 4. Every other claimed event references its old customer's id, which step 2 turned into a
        //    person record. An Available/Blocked row has neither and stays null.
        migrationBuilder.Sql(
            """
            UPDATE events SET person_id = customer_id
            WHERE person_id IS NULL AND customer_id IS NOT NULL
            """);

        // 5. The reveal ledger names the same ids under their new name. Hand-written table (`23-12`),
        //    so this is a raw rename rather than an EF-scaffolded one.
        migrationBuilder.Sql("ALTER TABLE contact_phone_reveals RENAME COLUMN customer_id TO person_id");

        // 6. The `23-60` merge ledger goes with the calendar-side merge it recorded (author decision
        //    O2 on `adr/0184`). Hand-written table, so dropped by hand - the snapshot never knew it.
        migrationBuilder.Sql("DROP TABLE IF EXISTS customer_merges");

        // 7. Now the copy itself: the event's old reference, then the table.
        migrationBuilder.DropForeignKey(
            name: "FK_events_customers_customer_id",
            table: "events");

        migrationBuilder.DropIndex(
            name: "IX_events_customer_id",
            table: "events");

        migrationBuilder.DropColumn(
            name: "customer_id",
            table: "events");

        migrationBuilder.DropTable(
            name: "customers");

        // 8. The booking's one person reference becomes a real foreign key - every row that has one
        //    now resolves, by construction of steps 2-4.
        migrationBuilder.CreateIndex(
            name: "IX_events_person_id",
            table: "events",
            column: "person_id");

        migrationBuilder.AddForeignKey(
            name: "FK_events_person_records_person_id",
            table: "events",
            column: "person_id",
            principalTable: "person_records",
            principalColumn: "person_id");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_events_person_records_person_id",
            table: "events");

        migrationBuilder.DropIndex(
            name: "IX_events_person_id",
            table: "events");

        migrationBuilder.AddColumn<Guid>(
            name: "customer_id",
            table: "events",
            type: "uuid",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "customers",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                first_seen_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                last_seen_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                merged_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                merged_into_customer_id = table.Column<Guid>(type: "uuid", nullable: true),
                no_show_count = table.Column<int>(type: "integer", nullable: false),
                notes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                phone = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                operator_confirmed_phone_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                phone_verified_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "Booking"),
                source_contact_id = table.Column<Guid>(type: "uuid", nullable: true),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_customers", x => x.id);
                table.ForeignKey(
                    name: "FK_customers_customers_merged_into_customer_id",
                    column: x => x.merged_into_customer_id,
                    principalTable: "customers",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_customers_tenants_tenant_id",
                    column: x => x.tenant_id,
                    principalTable: "tenants",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        // Shape only, literally: person records are NOT copied back into `customers`. The reshape
        // deliberately allows two records under one (tenant, phone) - a state the old
        // ux_customers_tenant_phone would refuse - and a Down that carries some rows and fails on others
        // is worse than one that honestly restores the empty shape. `events.customer_id` therefore comes
        // back null everywhere; `events.person_id` keeps its values (the `26-136` migration's own Down is
        // what removes that column).
        migrationBuilder.Sql("ALTER TABLE contact_phone_reveals RENAME COLUMN person_id TO customer_id");

        migrationBuilder.Sql(
            """
            CREATE TABLE customer_merges (
                id uuid NOT NULL,
                tenant_id uuid NOT NULL,
                survivor_customer_id uuid NOT NULL,
                absorbed_customer_id uuid NOT NULL,
                operator_id uuid NOT NULL,
                merged_at timestamptz NOT NULL,
                bookings_moved integer NOT NULL,
                CONSTRAINT "PK_customer_merges" PRIMARY KEY (id),
                CONSTRAINT "FK_customer_merges_tenants_tenant_id" FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE CASCADE,
                CONSTRAINT "FK_customer_merges_customers_survivor_customer_id" FOREIGN KEY (survivor_customer_id) REFERENCES customers (id) ON DELETE CASCADE,
                CONSTRAINT "FK_customer_merges_customers_absorbed_customer_id" FOREIGN KEY (absorbed_customer_id) REFERENCES customers (id) ON DELETE CASCADE
            );
            CREATE INDEX ix_customer_merges_tenant_id_id ON customer_merges (tenant_id, id);
            CREATE INDEX ix_customer_merges_survivor_customer_id ON customer_merges (survivor_customer_id);
            CREATE INDEX ix_customer_merges_absorbed_customer_id ON customer_merges (absorbed_customer_id);
            """);

        migrationBuilder.DropTable(
            name: "person_records");

        migrationBuilder.CreateIndex(
            name: "IX_events_customer_id",
            table: "events",
            column: "customer_id");

        migrationBuilder.CreateIndex(
            name: "IX_customers_merged_into_customer_id",
            table: "customers",
            column: "merged_into_customer_id");

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

        migrationBuilder.AddForeignKey(
            name: "FK_events_customers_customer_id",
            table: "events",
            column: "customer_id",
            principalTable: "customers",
            principalColumn: "id");
    }
}
