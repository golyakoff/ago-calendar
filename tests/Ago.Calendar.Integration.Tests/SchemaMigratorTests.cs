using Ago.Calendar.Infrastructure.Postgres.Schema;
using Ago.Calendar.Migrator;
using Npgsql;

namespace Ago.Calendar.Integration.Tests;

/// <summary>
/// `20-20`'s core done-when items, against a real Postgres: the migrator applies pending migrations
/// and exits zero, exits non-zero when a migration genuinely cannot be applied, a second run is a
/// no-op, and <c>--verify</c> reports without changing anything. Ported from
/// <c>Ago.Chat.Integration.Tests.SchemaMigratorTests</c> (`8-08`).
///
/// <para>Driven through <see cref="MigratorRunner"/> rather than by spawning the process, because
/// what these assert is the exit-code contract and the report - and both are that class's return
/// value and its <see cref="TextWriter"/>.</para>
///
/// <para><b>Narrower than the file it is ported from.</b> Ago.Chat's version also covers
/// <c>DatabaseAvailabilityWait</c> (`8-10`) - a migrator that waits out a Postgres still mid-restart
/// rather than failing immediately. This repository's <see cref="MigratorRunner"/> does not have that
/// mechanism (see its own remarks for why `20-20` deliberately did not port it pre-emptively), so
/// there is nothing here to test the wait behaviour of.</para>
/// </summary>
[Collection(SchemaCollection.Name)]
public class SchemaMigratorTests(SchemaFixture fixture)
{
    private async Task<(int ExitCode, string Output)> RunAsync(MigratorMode mode = MigratorMode.Apply)
    {
        await using var output = new StringWriter();
        var exitCode = await MigratorRunner.RunAsync(
            fixture.ConnectionString, mode, output, CancellationToken.None);
        return (exitCode, output.ToString());
    }

    [Fact]
    public async Task AnEmptyDatabase_IsMigratedAndExitsZero()
    {
        await fixture.ResetToAsync(null);

        var (exitCode, output) = await RunAsync();

        Assert.Equal(MigratorRunner.Success, exitCode);
        Assert.Contains($"Applied {fixture.AllMigrations.Count} migration(s)", output, StringComparison.Ordinal);
        // Names what it did, not just that it finished - a migration that runs silently is the same
        // operational problem as one that does not run.
        Assert.Contains(fixture.AllMigrations[0], output, StringComparison.Ordinal);
        Assert.Contains(fixture.AllMigrations[^1], output, StringComparison.Ordinal);

        await using var db = fixture.CreateDbContext();
        var status = await new SchemaVersionCheck(db).InspectAsync(CancellationToken.None);
        Assert.True(status.IsCurrent);
    }

    /// <summary>
    /// The idempotency done-when, and the reason a migrator Job can run on every deploy rather than
    /// only on the ones that need it - a conditional deploy step is a step that gets skipped, which is
    /// exactly the shape of the 2026-08-25 ago-chat incident that motivated `adr/0056` in the first
    /// place.
    ///
    /// <para>Proven by running it twice and reading the second run's own report, not by asserting that
    /// <c>__EFMigrationsHistory</c> exists and trusting EF to consult it.</para>
    /// </summary>
    [Fact]
    public async Task RunningItTwice_AppliesNothingTheSecondTime()
    {
        await fixture.ResetToAsync(null);

        var first = await RunAsync();
        var second = await RunAsync();

        Assert.Equal(MigratorRunner.Success, first.ExitCode);
        Assert.Equal(MigratorRunner.Success, second.ExitCode);
        Assert.Contains("Applied", first.Output, StringComparison.Ordinal);
        Assert.Contains("nothing to do", second.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Applied", second.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUpToDateDatabase_ExitsZeroAndAppliesNothing()
    {
        await fixture.ResetToCurrentAsync();

        var (exitCode, output) = await RunAsync();

        Assert.Equal(MigratorRunner.Success, exitCode);
        Assert.Contains("Schema already current", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A migration that genuinely cannot be applied. The database is not empty and not migrated: it
    /// already holds a <c>tenants</c> table of somebody else's shape (the first table this product's
    /// initial migration creates - see <c>AgoCalendarDbContextModelSnapshot</c>), so the first
    /// migration's <c>CREATE TABLE</c> fails on a real Postgres error rather than a simulated one.
    ///
    /// <para>The exit code is what a Kubernetes Job would read (the manifest itself is the next wave's
    /// work, out of this item's scope) - `adr/0056` requires it to stop the deploy rather than be
    /// retried into a crash loop. The assertion on the message matters too: a failed run whose logs say
    /// only "failed" would leave an operator no better off than the 2026-08-25 incident this pattern
    /// exists to avoid repeating.</para>
    /// </summary>
    [Fact]
    public async Task AMigrationThatCannotBeApplied_ExitsNonZeroAndSaysWhy()
    {
        await fixture.ResetToAsync(null);
        await using (var connection = new NpgsqlConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync();
            await using var blocker = new NpgsqlCommand("create table tenants (unexpected int);", connection);
            await blocker.ExecuteNonQueryAsync();
        }

        var (exitCode, output) = await RunAsync();

        Assert.Equal(MigratorRunner.Failure, exitCode);
        Assert.Contains("MIGRATION FAILED", output, StringComparison.Ordinal);
        Assert.Contains("tenants", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyMode_ExitsNonZeroWhenBehind_AndNamesThePendingMigration()
    {
        await fixture.ResetToOneMigrationBehindAsync();

        var (exitCode, output) = await RunAsync(MigratorMode.Verify);

        Assert.Equal(MigratorRunner.Failure, exitCode);
        Assert.Contains("Schema is behind", output, StringComparison.Ordinal);
        Assert.Contains(fixture.AllMigrations[^1], output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyMode_ExitsZeroWhenCurrent_AndChangesNothing()
    {
        await fixture.ResetToOneMigrationBehindAsync();
        var beforeVerify = await CountAppliedAsync();

        var behind = await RunAsync(MigratorMode.Verify);
        Assert.Equal(MigratorRunner.Failure, behind.ExitCode);
        // The read-only half of the contract: --verify reports and never repairs. If it applied
        // anything, the count would have moved and the deploy would have a second thing that migrates.
        Assert.Equal(beforeVerify, await CountAppliedAsync());

        await fixture.ResetToCurrentAsync();
        var current = await RunAsync(MigratorMode.Verify);

        Assert.Equal(MigratorRunner.Success, current.ExitCode);
        Assert.Contains("Schema is current", current.Output, StringComparison.Ordinal);
    }

    private async Task<int> CountAppliedAsync()
    {
        await using var db = fixture.CreateDbContext();
        var status = await new SchemaVersionCheck(db).InspectAsync(CancellationToken.None);
        return status.Applied.Count;
    }
}
