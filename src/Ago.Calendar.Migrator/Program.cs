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

// Ctrl-C / SIGTERM cancels the run - Postgres runs the DDL transactionally, so an interrupted apply
// rolls its current migration back and the history table stays truthful about what completed.
using var lifetime = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    lifetime.Cancel();
};

return await MigratorRunner.RunAsync(connectionString, mode, Console.Out, lifetime.Token);
