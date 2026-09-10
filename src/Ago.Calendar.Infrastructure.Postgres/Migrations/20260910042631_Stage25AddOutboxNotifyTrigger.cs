using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Calendar.Infrastructure.Postgres.Migrations;

/// <inheritdoc />
public partial class Stage25AddOutboxNotifyTrigger : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // `25-44`: lets OutboxDispatcher wake on INSERT instead of waiting out its poll interval
        // (messaging.md's "poll-plus-notify") - the identical trigger
        // `Ago.Chat.Infrastructure.Postgres.Migrations.Stage2AddOutboxNotifyTrigger` already adds
        // for chat's own outbox table, restated here for this product's own, separate database
        // (`adr/0027`: a separate database per product, so this is not the same trigger reused -
        // it is the identical SQL, applied a second time). A database trigger, not a change to
        // the shared EfOutboxWriter<TDbContext> that stages a row in the first place: this needs
        // no code change to the writer at all, and the notification carries no payload the
        // dispatcher trusts (it just means "go look"), so a stale or missed notification is
        // harmless by construction (the poll interval remains the fallback).
        migrationBuilder.Sql("""
            CREATE OR REPLACE FUNCTION notify_outbox_insert() RETURNS trigger AS $$
            BEGIN
                PERFORM pg_notify('outbox_new_row', NEW.id::text);
                RETURN NEW;
            END;
            $$ LANGUAGE plpgsql;
            """);

        migrationBuilder.Sql("""
            CREATE TRIGGER outbox_notify_trigger
            AFTER INSERT ON outbox
            FOR EACH ROW EXECUTE FUNCTION notify_outbox_insert();
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP TRIGGER IF EXISTS outbox_notify_trigger ON outbox;");
        migrationBuilder.Sql("DROP FUNCTION IF EXISTS notify_outbox_insert();");
    }
}
