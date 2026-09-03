using System.Diagnostics;
using System.Net.Sockets;
using Ago.Calendar.Infrastructure.Postgres.Schema;
using Ago.Calendar.Migrator;
using Npgsql;
using Testcontainers.PostgreSql;

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
/// <para><b>`ago-root#340`: the connectivity-wait tests, added.</b> `20-20`'s own report named the
/// gap this closes: the migrator's correctness depended on <c>ago-deploy</c>'s init container waiting
/// for Postgres on its behalf, and nothing said so. The tests below are ported unchanged in shape from
/// the ones <c>Ago.Chat.Integration.Tests.SchemaMigratorTests</c> (`8-10`) added for the identical
/// mechanism in AGO Chat.</para>
/// </summary>
[Collection(SchemaCollection.Name)]
public class SchemaMigratorTests(SchemaFixture fixture)
{
    /// <summary>
    /// `ago-root#340`: a budget long enough that spending any of it is visible in a stopwatch. Every
    /// test below that asserts "this did not wait" passes this, so the assertion is about behaviour
    /// rather than about the default happening to be small.
    /// </summary>
    private static readonly DatabaseAvailabilityOptions Patient = new()
    {
        WaitTimeout = TimeSpan.FromMinutes(2),
        PollInterval = TimeSpan.FromSeconds(2),
    };

    private async Task<(int ExitCode, string Output)> RunAsync(
        MigratorMode mode = MigratorMode.Apply, DatabaseAvailabilityOptions? wait = null)
    {
        await using var output = new StringWriter();
        var exitCode = await MigratorRunner.RunAsync(
            fixture.ConnectionString, mode, output, CancellationToken.None, wait);
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

        var elapsed = Stopwatch.StartNew();
        var (exitCode, output) = await RunAsync(wait: Patient);

        Assert.Equal(MigratorRunner.Failure, exitCode);
        Assert.Contains("MIGRATION FAILED", output, StringComparison.Ordinal);
        Assert.Contains("tenants", output, StringComparison.Ordinal);

        // `ago-root#340`'s constraint on itself: a genuinely failing migration must still fail
        // immediately, without waiting and without retrying. That is the property `20-21`'s guard
        // depends on and the one most easily eroded by wrapping a wait one level too wide, so it is
        // asserted three ways - the failure is a MIGRATION failure and not a connectivity one, no wait
        // was ever announced, and a two-minute budget was on offer and none of it was spent.
        Assert.DoesNotContain("WAITING FOR DATABASE FAILED", output, StringComparison.Ordinal);
        Assert.DoesNotContain("not accepting connections yet", output, StringComparison.Ordinal);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(20),
            $"a failing migration must not consume the connectivity budget; it took {elapsed.Elapsed}");
    }

    /// <summary>
    /// `ago-root#340`'s second done-when: a database that never arrives still stops the run, within
    /// the budget, <b>with a message naming waiting as what failed rather than the migration</b>.
    /// Before this item the same connection string produced <c>MIGRATION FAILED</c>, which is what made
    /// an infrastructure problem and a code problem indistinguishable in the logs.
    /// </summary>
    [Fact]
    public async Task ADatabaseThatNeverArrives_ExitsNonZeroNamingTheWaitRatherThanTheMigration()
    {
        await using var output = new StringWriter();
        var wait = new DatabaseAvailabilityOptions
        {
            WaitTimeout = TimeSpan.FromSeconds(2),
            PollInterval = TimeSpan.FromMilliseconds(200),
        };
        var elapsed = Stopwatch.StartNew();

        var exitCode = await MigratorRunner.RunAsync(
            "Host=127.0.0.1;Port=1;Database=nope;Username=nope;Password=not-a-real-password;Timeout=2",
            MigratorMode.Apply, output, CancellationToken.None, wait);

        var report = output.ToString();
        Assert.Equal(MigratorRunner.Failure, exitCode);
        Assert.Contains("WAITING FOR DATABASE FAILED", report, StringComparison.Ordinal);
        Assert.Contains("gave up after", report, StringComparison.Ordinal);
        // Names where, so the reader can tell "Postgres is down" from "this run is pointed at the
        // wrong Postgres" - and names the provider's own error rather than a summary of it.
        Assert.Contains("127.0.0.1:1/nope", report, StringComparison.Ordinal);
        Assert.Contains("No migration was attempted", report, StringComparison.Ordinal);
        Assert.DoesNotContain("MIGRATION FAILED", report, StringComparison.Ordinal);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(30),
            $"giving up must be bounded by WaitTimeout; it took {elapsed.Elapsed}");
    }

    /// <summary>
    /// <b>The open question this item exists to answer, against a real Postgres.</b> "A wait that
    /// swallows an authentication failure turns a wrong password into a timeout, and that is a worse
    /// error message than the one this item is fixing."
    ///
    /// <para>Same running server as every other test here, same host and port - only the credential is
    /// wrong, so this cannot pass by accident of the server being unreachable. A two-minute budget is
    /// on offer and the assertion is that none of it is spent.</para>
    /// </summary>
    [Fact]
    public async Task AWrongPassword_IsReportedAtOnceRatherThanWaitedOut()
    {
        await fixture.ResetToCurrentAsync();
        var wrongCredential = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            Password = "not-the-password-340",
        }.ConnectionString;

        await using var output = new StringWriter();
        var elapsed = Stopwatch.StartNew();

        var exitCode = await MigratorRunner.RunAsync(
            wrongCredential, MigratorMode.Apply, output, CancellationToken.None, Patient);

        var report = output.ToString();
        Assert.Equal(MigratorRunner.Failure, exitCode);
        Assert.Contains("CANNOT CONNECT TO DATABASE", report, StringComparison.Ordinal);
        Assert.Contains("28P01", report, StringComparison.Ordinal);
        Assert.DoesNotContain("not accepting connections yet", report, StringComparison.Ordinal);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(20),
            $"a wrong password must not consume the wait budget; it took {elapsed.Elapsed}");
        // The report names where it was going; it must never name what it was going with.
        Assert.DoesNotContain("not-the-password-340", report, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>`ago-root#340`'s first done-when, proven by starting it first - not by reading the
    /// code.</b>
    ///
    /// <para>This reproduces the shape of failure `8-10` closed for AGO Chat: the migrator is running
    /// against <c>127.0.0.1:port</c> before anything is listening there, exactly as a Job would start
    /// while a Postgres pod was still restarting. It then walks the real sequence of failures -
    /// <c>Connection refused</c> while nothing holds the port, a connection accepted and dropped
    /// mid-handshake once Docker's port proxy binds it, <c>57P03 the database system is starting up</c>
    /// while Postgres initialises - and succeeds.</para>
    ///
    /// <para>Its own container rather than the collection fixture's, because the fixture's Postgres is
    /// already accepting connections by the time any test runs, and a wait cannot be demonstrated
    /// against a database that is already there. It does <b>not</b> take
    /// <see cref="DockerResourceLock"/>: <see cref="SchemaFixture"/> holds that for this whole
    /// collection's lifetime, so acquiring it here would deadlock against the fixture that made this
    /// test possible.</para>
    /// </summary>
    [Fact]
    public async Task AMigratorStartedBeforePostgres_WaitsAndThenSucceeds()
    {
        var port = GetFreeTcpPort();
        // Not a secret and deliberately shaped so it cannot be mistaken for one if it is ever seen
        // outside this file: this container exists for the length of one test method.
        const string password = "wait-test-only-not-a-secret";
        var connectionString =
            $"Host=127.0.0.1;Port={port};Database=ago_calendar_wait;Username=ago;Password={password};Timeout=3";

        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine")
            .WithPortBinding(port, 5432)
            .WithDatabase("ago_calendar_wait")
            .WithUsername("ago")
            .WithPassword(password)
            .Build();

        await using var output = new StringWriter();

        // The migrator starts first. Nothing is listening on that port yet, and nothing has been asked
        // to listen on it.
        var migrator = Task.Run(() => MigratorRunner.RunAsync(
            connectionString,
            MigratorMode.Apply,
            output,
            CancellationToken.None,
            new DatabaseAvailabilityOptions
            {
                WaitTimeout = TimeSpan.FromMinutes(2),
                PollInterval = TimeSpan.FromMilliseconds(500),
            }));

        // Long enough for several probes to fail for real. Before this item the process would already
        // have exited non-zero by now, which is the whole point of asserting it has not.
        await Task.Delay(TimeSpan.FromSeconds(4));
        Assert.False(migrator.IsCompleted,
            "the migrator exited instead of waiting for a database that was not there yet");

        await postgres.StartAsync();

        var exitCode = await migrator.WaitAsync(TimeSpan.FromMinutes(3));
        var report = output.ToString();

        Assert.Equal(MigratorRunner.Success, exitCode);
        Assert.Contains("not accepting connections yet", report, StringComparison.Ordinal);
        Assert.Contains("accepted a connection after", report, StringComparison.Ordinal);
        // And it did the job it was started for, rather than merely surviving the wait.
        Assert.Contains($"Applied {fixture.AllMigrations.Count} migration(s)", report, StringComparison.Ordinal);
        Assert.DoesNotContain("WAITING FOR DATABASE FAILED", report, StringComparison.Ordinal);
        Assert.DoesNotContain("MIGRATION FAILED", report, StringComparison.Ordinal);
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
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
