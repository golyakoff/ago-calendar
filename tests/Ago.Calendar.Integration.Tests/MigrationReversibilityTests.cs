using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Ago.Calendar.Integration.Tests;

/// <summary>
/// data-model.md: "always reversible, or explicitly marked one-way with a comment explaining why".
/// This one is reversible, and that claim is worth exactly as much as a test that runs the Down.
///
/// <para>The hand-written half is what makes this more than a formality: EF generated the tables and
/// would revert them regardless, but the exclusion constraint and the <c>btree_gist</c> extension
/// were added with raw SQL, and raw SQL in an Up with nothing matching it in the Down is the
/// ordinary way a migration stops being reversible without anybody noticing.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public class MigrationReversibilityTests(PostgresFixture fixture)
{
    [Fact]
    public async Task TheSchemaMigration_RevertsCompletely_AndReapplies()
    {
        await using (var db = fixture.CreateDbContext())
        {
            // "0" is EF's own name for "before the first migration" - a full teardown.
            var migrator = db.GetInfrastructure().GetRequiredService<IMigrator>();
            await migrator.MigrateAsync("0");
        }

        Assert.False(await TableExistsAsync("events"));
        Assert.False(await ConstraintExistsAsync("ex_events_worker_no_overlap"));
        Assert.False(await ExtensionExistsAsync("btree_gist"));

        await using (var db = fixture.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        // Back exactly as it was, hand-written pieces included - which is the part a
        // DropTable-only Down would have silently got wrong.
        Assert.True(await TableExistsAsync("events"));
        Assert.True(await ConstraintExistsAsync("ex_events_worker_no_overlap"));
        Assert.True(await ExtensionExistsAsync("btree_gist"));

        await using var reader = fixture.CreateDbContext();
        Assert.False(reader.Database.HasPendingModelChanges());
    }

    private Task<bool> TableExistsAsync(string table) =>
        ScalarBoolAsync($"SELECT to_regclass('public.{table}') IS NOT NULL");

    private Task<bool> ConstraintExistsAsync(string constraint) =>
        ScalarBoolAsync($"SELECT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = '{constraint}')");

    private Task<bool> ExtensionExistsAsync(string extension) =>
        ScalarBoolAsync($"SELECT EXISTS (SELECT 1 FROM pg_extension WHERE extname = '{extension}')");

    private async Task<bool> ScalarBoolAsync(string sql)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (bool)(await command.ExecuteScalarAsync())!;
    }
}
