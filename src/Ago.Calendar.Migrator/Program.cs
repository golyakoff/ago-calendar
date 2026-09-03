using Ago.Calendar.Infrastructure.Postgres.Schema;
using Ago.Calendar.Migrator;

// `20-20`/`adr/0056`: the deployable that applies migrations, and the only thing that does - this
// product's own equivalent of `Ago.Chat.Migrator` (`8-08`).
//
// Argument parsing and a connection string; everything else is MigratorRunner, which a test drives
// against a real Postgres. No generic host, no DI container, no configuration binding: this process
// opens a connection, applies what is pending, says what it did, and exits.

// Same variable name shape every Ago.Calendar.* host would read (AGO_CALENDAR_CONNECTION_STRING,
// matching AgoCalendarDbContextFactory's own design-time variable), so a manifest and the
// docker-compose loop can hand the migrator the same value they hand the Api.
var connectionString = Environment.GetEnvironmentVariable("AGO_CALENDAR_CONNECTION_STRING");
if (string.IsNullOrWhiteSpace(connectionString))
{
    await Console.Error.WriteLineAsync(
        "Set AGO_CALENDAR_CONNECTION_STRING - e.g. Host=localhost;Port=5432;Database=ago_calendar;"
        + "Username=...;Password=...");
    return MigratorRunner.Failure;
}

// --verify is the read-only mode. There is deliberately no --down and no --target: EF generates
// Down() methods and this project has never executed one, so offering a rollback flag would be
// offering a path nobody has tested, which is worse than offering none because it would be believed.
var mode = args.Contains("--verify", StringComparer.Ordinal) ? MigratorMode.Verify : MigratorMode.Apply;

var unknown = args.Where(a => a is not "--verify").ToList();
if (unknown.Count > 0)
{
    await Console.Error.WriteLineAsync(
        $"Unknown argument(s): {string.Join(", ", unknown)}. Usage: Ago.Calendar.Migrator [--verify]");
    return MigratorRunner.Failure;
}

// `ago-root#340`: the one *optional* variable, added alongside the required connection string above.
// Unset means the chosen 90s default, so the set of variables the migrator *requires* is still exactly
// one. An unparseable value is refused rather than silently defaulted - a manifest typo that quietly
// restored the default would be the same class of drift `20-21`'s schema guard exists to prevent.
if (!DatabaseAvailabilityOptions.TryReadFromEnvironment(
        Environment.GetEnvironmentVariable, out var waitOptions, out var waitError))
{
    await Console.Error.WriteLineAsync(waitError);
    return MigratorRunner.Failure;
}

// Ctrl-C / SIGTERM cancels the wait for a connection, not a migration already in flight - Postgres
// runs the DDL transactionally, so an interrupted apply rolls its current migration back and the
// history table stays truthful about what completed.
using var lifetime = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    lifetime.Cancel();
};

return await MigratorRunner.RunAsync(connectionString, mode, Console.Out, lifetime.Token, waitOptions);
