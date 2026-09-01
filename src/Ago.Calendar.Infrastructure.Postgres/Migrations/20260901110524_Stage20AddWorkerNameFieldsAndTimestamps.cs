using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Calendar.Infrastructure.Postgres.Migrations;

/// <summary>
/// `20-13`: splits <c>workers.display_name</c> into <c>last_name</c>/<c>first_name</c>/<c>middle_name</c>
/// plus the <c>display_name_is_custom</c> flag that stops <c>Worker.Rename</c> recomputing it, and
/// gives the aggregate <c>created_at</c>/<c>updated_at</c> - the one migration this item owns, per its
/// own budget of exactly one.
///
/// <para><b>Backfill decision (the item's own "open question").</b> Every row that exists before this
/// migration runs has only a display name - <c>Worker.LastName</c> and <c>Worker.FirstName</c> are
/// new, required columns with nothing to source <c>Worker.FirstName</c> from. Splitting the existing
/// display name on its first space would
/// guess at a real name and get it wrong for exactly the case that matters most: a one-person shop
/// whose "worker" is the business's own one-word name (no space to split on at all). The chosen
/// backfill is honest instead of clever: <c>last_name</c> becomes the whole existing display name
/// unchanged, <c>first_name</c> becomes the placeholder <c>"—"</c> (an em dash - visible, not a blank
/// a form field would silently accept as "already filled in"), and <c>display_name_is_custom</c> is
/// set <see langword="true"/> so a future <c>Rename</c> - typing a real first name once an operator
/// notices the placeholder - does not also have to fight the display name back from
/// <c>"— &lt;last name&gt;"</c> to the name the tenant already had. The console surfaces
/// <c>first_name = "—"</c> as a row that needs a real correction; nothing in the API rejects it,
/// because the migration writes it with raw SQL, which does not run <c>Worker</c>'s own constructor
/// validation at all.</para>
///
/// <para><b><c>created_at</c>/<c>updated_at</c> come from the row's own id, not from <c>now()</c>.</b>
/// <c>WorkerId</c> is a UUIDv7 minted by <c>Ago.Platform.Kernel.UuidV7Generator</c>, whose leading 48
/// bits are a millisecond Unix timestamp - the same ordering property `20-12`'s own migration
/// (<c>Stage20AddAccountOwnerAndContactVisibility</c>) used to find "the earliest operator per
/// tenant" by sorting on <c>id</c>. This migration goes one step further and actually decodes that
/// timestamp back into a <c>timestamptz</c>, because a fabricated <c>now()</c> in a column a tenant
/// reads ("when was this worker added?") would be a lie the day after this deploys. Postgres has no
/// hex-string-to-integer literal syntax usable at runtime (only the parser-level <c>X'...'</c>
/// literal, which cannot take a computed argument), so the extraction goes through
/// <c>uuid_send()</c> - the well-documented function that returns a UUID's 16 raw bytes as
/// <c>bytea</c> in network (big-endian) order - and <c>get_byte()</c> to read the first six of them,
/// which is exactly RFC 9562's <c>unix_ts_ms</c> field.</para>
/// </summary>
public partial class Stage20AddWorkerNameFieldsAndTimestamps : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Every AddColumn below carries a placeholder default so the statement succeeds against rows
        // that already exist; the backfill UPDATE that follows overwrites every one of those defaults
        // for every row present today (there is no WHERE clause distinguishing "old" from "new" rows
        // here, because at migration time every row is an old one - a worker created after this
        // migration runs always goes through Worker.Create, which never leaves these placeholders
        // behind).
        migrationBuilder.AddColumn<string>(
            name: "last_name",
            table: "workers",
            type: "character varying(100)",
            maxLength: 100,
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<string>(
            name: "first_name",
            table: "workers",
            type: "character varying(100)",
            maxLength: 100,
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<string>(
            name: "middle_name",
            table: "workers",
            type: "character varying(100)",
            maxLength: 100,
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "display_name_is_custom",
            table: "workers",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "created_at",
            table: "workers",
            type: "timestamptz",
            nullable: false,
            defaultValue: DateTimeOffset.UnixEpoch);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "updated_at",
            table: "workers",
            type: "timestamptz",
            nullable: false,
            defaultValue: DateTimeOffset.UnixEpoch);

        // Backfill every existing row: last_name <- the old display_name verbatim, first_name <- the
        // "needs a real correction" placeholder, display_name_is_custom <- true (so the display name
        // just preserved is never silently recomputed into "— <last name>" by a later Rename), and
        // created_at/updated_at <- the millisecond timestamp already encoded in the row's own UUIDv7
        // id.
        //
        // get_byte(uuid_send(id), n): uuid_send() returns the UUID's 16 bytes as bytea in network
        // (big-endian) order - the same byte order RFC 9562 defines the UUID's own fields in - and
        // get_byte() reads one of them as an integer 0-255. Bytes 0-5 are UUIDv7's unix_ts_ms field,
        // reassembled here with bit shifts because Postgres has no runtime hex-string-to-integer cast
        // (only the parser-level X'...' literal, which cannot take a computed string). to_timestamp()
        // takes seconds, hence the /1000.0.
        migrationBuilder.Sql(
            """
            WITH minted AS (
                SELECT
                    id,
                    to_timestamp(
                        (
                            (get_byte(uuid_send(id), 0)::bigint << 40) |
                            (get_byte(uuid_send(id), 1)::bigint << 32) |
                            (get_byte(uuid_send(id), 2)::bigint << 24) |
                            (get_byte(uuid_send(id), 3)::bigint << 16) |
                            (get_byte(uuid_send(id), 4)::bigint << 8) |
                            (get_byte(uuid_send(id), 5)::bigint)
                        ) / 1000.0
                    ) AS minted_at
                FROM workers
            )
            UPDATE workers
            SET last_name = workers.display_name,
                first_name = '—',
                display_name_is_custom = true,
                created_at = minted.minted_at,
                updated_at = minted.minted_at
            FROM minted
            WHERE workers.id = minted.id
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "last_name",
            table: "workers");

        migrationBuilder.DropColumn(
            name: "first_name",
            table: "workers");

        migrationBuilder.DropColumn(
            name: "middle_name",
            table: "workers");

        migrationBuilder.DropColumn(
            name: "display_name_is_custom",
            table: "workers");

        migrationBuilder.DropColumn(
            name: "created_at",
            table: "workers");

        migrationBuilder.DropColumn(
            name: "updated_at",
            table: "workers");
    }
}
